using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;

namespace Shards.Engine
{
    /// <summary>
    /// The Shards of Infinity rules machine. Same single-pending-input discipline as the
    /// Pascension engine but WITHOUT a stack/priority system: plays resolve immediately;
    /// the only cross-player interaction is the shield-reveal decision during end-turn
    /// damage assignment. All mutation flows through Submit; effects are iterators that
    /// pause on decisions; every change lands in the EventLog with per-viewer redaction.
    /// </summary>
    public sealed class ShardsEngine
    {
        public readonly ShardsState State;
        public readonly EventLog Log = new();

        public PendingInput PendingInput { get; private set; }

        // Active effect resolution (paused on a decision) + the follow-up queue.
        private IEnumerator<ShardsStep> _activeEffect;
        private ShardsContext _activeContext;
        private readonly Queue<(IShardsEffect effect, ShardsContext ctx)> _effectQueue = new();

        // End-turn flow state (damage split → per-defender shield reveals → cleanup).
        private bool _endTurnInProgress;
        // Public flow phase only; exposes no card identities or hidden choices.
        public bool IsResolvingEndTurn => _endTurnInProgress;
        private Queue<(int defender, int amount)> _pendingDefenses;
        /// <summary>Testudo Vanguard (Duel): champion hits deferred into their owner's
        /// defense step so revealed shields reduce them individually. Transient within one
        /// end-turn resolution (like _pendingDefenses) — never cloned or hashed.</summary>
        private Dictionary<int, List<(int hitId, int amount)>> _pendingChampionHits;
        private List<int> _splitTargets;
        private List<int> _splitAmounts;
        /// <summary>Initial-fill exclusions held privately and shuffled back afterward:
        /// monsters in all formats, and Duel cards costing 6+ except Comet.</summary>
        private List<ShardsCard> _suppressedOpeningCards;

        public ShardsEngine(ShardsConfig config)
        {
            State = new ShardsState();
            Setup(config);
        }

        // ------------------------------------------------------------------ setup

        private void Setup(ShardsConfig config)
        {
            // Duel of Doom requires the other three DLCs — normalize the flag so the whole
            // stack (center deck, relics, errata swaps, rules) agrees on one mask.
            config.Dlc = NormalizeDlc(config.Dlc);

            State.Rules = config.Rules;
            State.Dlc = config.Dlc;
            State.Rng = new DeterministicRng(config.Seed);

            // Errata swap: any enabled Duel def with a ReplacesId skips that base def from
            // the pool (same mechanism as cloud_oracles → cloud_oracles_sos, generalized).
            var replaced = ReplacedIds(config.Dlc);

            // Center deck from the enabled sets (relics and destinies are never in it).
            foreach (var def in ShardsCardDatabase.All)
            {
                if (!InInitialCenterPool(def, config.Dlc, replaced)) continue;
                for (int i = 0; i < def.Quantity; i++)
                    State.CenterDeck.Add(NewCard(def.Id, -1, ShardsZone.CenterDeck));
            }
            State.Rng.Shuffle(State.CenterDeck);

            // ItH: shuffle the destiny deck and deal 6 face up as the shared Destiny Row
            // (the row only ever shrinks — destinies are taken, never refilled; Stolen
            // Futures can add more from the remaining deck).
            if ((config.Dlc & ShardsDlc.IntoTheHorizon) != 0)
            {
                var destinies = new List<ShardsCard>();
                foreach (var def in ShardsCardDatabase.All)
                {
                    if (def.Type != ShardsCardType.Destiny) continue;
                    if (replaced.Contains(def.Id)) continue; // errata'd out by a Duel replacement
                    // Duel destiny errata (Set "duel") only join when Duel is on; base
                    // destinies (Set "into_the_horizon") join whenever this loop runs.
                    if (def.Set == "duel" && (config.Dlc & ShardsDlc.Duel) == 0) continue;
                    for (int i = 0; i < def.Quantity; i++)
                        destinies.Add(NewCard(def.Id, -1, ShardsZone.DestinyRow));
                }
                State.Rng.Shuffle(destinies);
                for (int i = 0; i < destinies.Count; i++)
                {
                    if (i < 6) State.DestinyRow.Add(destinies[i]);
                    else State.DestinyDeck.Add(destinies[i]);
                }
            }

            // Duel of Doom drafts heroes on turn 1 (reverse seat order) AFTER the board is
            // built — so players create WITHOUT a character/relics; HeroDraftFlow assigns
            // both. Non-Duel keeps the lobby-assigned character and sets relics aside now.
            bool duel = (config.Dlc & ShardsDlc.Duel) != 0;

            // Players: starter decks, staggered mastery 0/1/2/3, opening hands.
            for (int i = 0; i < config.Players.Count; i++)
            {
                var spec = config.Players[i];
                var player = new ShardsPlayer
                {
                    Index = i,
                    Name = spec.Name,
                    CharacterId = duel ? null : spec.CharacterId, // draft assigns it
                    FullControl = spec.FullControl,
                    Health = config.Rules.StartingHealth,
                    Mastery = i, // staggered start: 0/1/2/3 by turn order
                    Gems = config.Players.Count == 2 && i == 1 ? 1 : 0
                };
                foreach (var def in ShardsCardDatabase.All)
                {
                    if (def.Type != ShardsCardType.Starter) continue;
                    for (int c = 0; c < def.Quantity; c++)
                        player.Deck.Add(NewCard(def.Id, i, ShardsZone.Deck));
                }
                State.Rng.Shuffle(player.Deck);

                if (!duel)
                    SetAsideRelicsFor(player, config.Dlc);

                State.Players.Add(player);
            }

            // Initial row: hold monsters privately in every format. Duel additionally
            // excludes printed-cost-6+ cards except Comet. Shuffle every excluded card
            // into the unrevealed remainder before any draft or reveal can inspect it.
            State.CenterRow = new ShardsCard[config.Rules.CenterRowSize];
            _suppressedOpeningCards = new List<ShardsCard>();
            for (int s = 0; s < State.CenterRow.Length; s++)
                RefillSlot(s);
            if (_suppressedOpeningCards.Count > 0)
            {
                foreach (var excluded in _suppressedOpeningCards)
                {
                    excluded.Zone = ShardsZone.CenterDeck;
                    State.CenterDeck.Add(excluded);
                }
                State.Rng.Shuffle(State.CenterDeck);
            }
            _suppressedOpeningCards = null;

            Emit(new ShardsGameStartedEvent { PlayerCount = config.Players.Count, Dlc = (int)config.Dlc });

            foreach (var player in State.Players)
                for (int d = 0; d < config.Rules.HandSize; d++)
                    DrawOne(player);

            if (duel)
            {
                // Draft heroes on turn 1, reverse seat order, on the initialized board.
                _draftDefaults = new List<string>();
                foreach (var spec in config.Players) _draftDefaults.Add(spec.CharacterId);
                QueueEffect(new Custom(HeroDraftFlow), 0, null);
                Pump();
            }
            else
            {
                StartTurn(0, firstTurn: true);
                RoutePriority();
            }
        }

        private static bool InInitialCenterPool(ShardsCardDef def, ShardsDlc dlc, HashSet<string> replaced)
        {
            if (def.Type == ShardsCardType.Starter ||
                def.Type == ShardsCardType.Relic ||
                def.Type == ShardsCardType.Destiny)
                return false;
            bool inSet = def.Set switch
            {
                "base" => true,
                "relics_of_the_future" => (dlc & ShardsDlc.RelicsOfTheFuture) != 0,
                "shadow_of_salvation" => (dlc & ShardsDlc.ShadowOfSalvation) != 0,
                "into_the_horizon" => (dlc & ShardsDlc.IntoTheHorizon) != 0,
                "duel" => (dlc & ShardsDlc.Duel) != 0,
                _ => false
            };
            if (!inSet) return false;
            if (replaced.Contains(def.Id)) return false; // errata'd out by a Duel replacement
            // ItH rule: Corruption's reward needs relics — remove it without RotF.
            if (def.IsMonster && def.Id == "ingeminex_corruption" &&
                (dlc & ShardsDlc.RelicsOfTheFuture) == 0)
                return false;
            // SoS ships errata replacements for RotF's Cloud Oracles: with both sets
            // enabled only the replacement copies play (PvP-identical wording fix).
            if (def.Id == "cloud_oracles" && (dlc & ShardsDlc.ShadowOfSalvation) != 0)
                return false;
            return true;
        }

        /// <summary>Public setup quantities, independent of hidden zones or later card
        /// creation. Starters count every seat; relics count the drafted heroes only.</summary>
        public Dictionary<string, int> InitialCardCounts()
        {
            var counts = new Dictionary<string, int>();
            var replaced = ReplacedIds(State.Dlc);
            foreach (var def in ShardsCardDatabase.All)
            {
                int count = 0;
                if (def.Type == ShardsCardType.Starter) count = def.Quantity * State.Players.Count;
                else if (def.Type == ShardsCardType.Relic)
                {
                    foreach (var player in State.Players)
                        if (player.CharacterId != null && RelicIdsFor(player.CharacterId, State.Dlc).Contains(def.Id)) count++;
                }
                else if (def.Type == ShardsCardType.Destiny)
                {
                    if ((State.Dlc & ShardsDlc.IntoTheHorizon) != 0 && !replaced.Contains(def.Id) &&
                        (def.Set != "duel" || (State.Dlc & ShardsDlc.Duel) != 0)) count = def.Quantity;
                }
                else if (InInitialCenterPool(def, State.Dlc, replaced)) count = def.Quantity;
                if (count > 0) counts.Add(def.Id, count);
            }
            return counts;
        }

        /// <summary>The relic def ids a character sets aside under the given DLC mask —
        /// shipped-set gating + Duel errata swaps applied. Deterministic order (sorted by
        /// id). Public: the hero-draft UI shows each hero's relics from the same source
        /// the engine deals from.</summary>
        public static List<string> RelicIdsFor(string characterId, ShardsDlc dlc)
        {
            var replaced = ReplacedIds(dlc);
            var ids = new List<string>();
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Type != ShardsCardType.Relic || def.Character != characterId) continue;
                if (replaced.Contains(def.Id)) continue; // errata'd out by a Duel replacement
                bool shipped = def.Set switch
                {
                    "relics_of_the_future" => (dlc & ShardsDlc.RelicsOfTheFuture) != 0,
                    "shadow_of_salvation" => (dlc & ShardsDlc.ShadowOfSalvation) != 0,
                    "duel" => (dlc & ShardsDlc.Duel) != 0,
                    _ => false
                };
                if (shipped)
                    ids.Add(def.Id);
            }
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        /// <summary>Set aside a player's relics (recruit ONE free at Mastery 10). Called at
        /// setup (non-Duel) or after a hero draft pick assigns the character (Duel).</summary>
        private void SetAsideRelicsFor(ShardsPlayer player, ShardsDlc dlc)
        {
            foreach (var id in RelicIdsFor(player.CharacterId, dlc))
                player.SetAside.Add(NewCard(id, player.Index, ShardsZone.SetAside));
        }

        // Duel of Doom hero draft: reverse seat order, no duplicates, on the built board.
        // Available characters for the hero pool
        // without duplicating the list.
        public static readonly string[] DraftableCharacters = { "decima", "tetra", "volos", "kosynwu", "rez" };
        private List<string> _draftDefaults;

        private IEnumerable<ShardsStep> HeroDraftFlow(ShardsContext ctx)
        {
            var heroes = new List<string>(DraftableCharacters);
            var taken = new HashSet<string>();
            // Last player picks first (compensates for the seat disadvantage).
            for (int seat = State.Players.Count - 1; seat >= 0; seat--)
            {
                var player = State.Players[seat];
                var available = heroes.FindAll(h => !taken.Contains(h));
                string preferred = _draftDefaults != null && seat < _draftDefaults.Count ? _draftDefaults[seat] : null;
                int defaultIdx = available.Contains(preferred) ? heroes.IndexOf(preferred) : heroes.IndexOf(available[0]);

                var req = new DecisionRequest
                {
                    PlayerIndex = seat,
                    Kind = DecisionKind.ChooseMode,
                    Title = "Choose your hero",
                    Context = "soi.herodraft",
                    Min = 1,
                    Max = 1
                };
                req.DefaultOptionIds.Add(defaultIdx);
                foreach (var h in available)
                    req.Options.Add(new DecisionOption(heroes.IndexOf(h), HeroDraftLabel(h)) { DefId = h });
                yield return ShardsStep.AwaitDecision(req);

                int pick = ctx.Answer.ChosenOptionIds.Count > 0 ? ctx.Answer.ChosenOptionIds[0] : defaultIdx;
                string chosen = (pick >= 0 && pick < heroes.Count && !taken.Contains(heroes[pick]))
                    ? heroes[pick] : available[0];
                taken.Add(chosen);
                player.CharacterId = chosen;
                SetAsideRelicsFor(player, State.Dlc);
                Emit(new ShardsHeroDraftedEvent { PlayerIndex = seat, CharacterId = chosen });
            }
            _draftDefaults = null;
            StartTurn(0, firstTurn: true);
        }

        private static string HeroDraftLabel(string id)
        {
            var spec = HeroAbilityInfo(id);
            return spec.Name == null ? id : $"{id} — {spec.Name}: {spec.Text}";
        }

        private ShardsCard NewCard(string defId, int owner, ShardsZone zone) => new()
        {
            InstanceId = State.NextInstanceId++,
            DefId = defId,
            Owner = owner,
            Zone = zone
        };

        /// <summary>Duel of Doom requires all three other DLCs; force them on so the pool,
        /// errata swaps and rules are consistent no matter how the flag was assembled.</summary>
        public static ShardsDlc NormalizeDlc(ShardsDlc dlc) =>
            (dlc & ShardsDlc.Duel) != 0
                ? dlc | ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon
                : dlc;

        /// <summary>Base-def ids replaced by an enabled Duel errata def (Set "duel" with a
        /// ReplacesId). Those base defs are skipped from the center deck and relic pool.</summary>
        private static HashSet<string> ReplacedIds(ShardsDlc dlc)
        {
            var replaced = new HashSet<string>();
            if ((dlc & ShardsDlc.Duel) == 0) return replaced;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Set == "duel" && !string.IsNullOrEmpty(def.ReplacesId))
                    replaced.Add(def.ReplacesId);
            return replaced;
        }

        // ------------------------------------------------------------------ submit pump

        public SubmitResult Submit(PlayerAction action)
        {
            if (State.GameOver)
                return SubmitResult.Rejected("The game is over");
            if (PendingInput == null)
                return SubmitResult.Rejected("No input expected");
            if (action.PlayerIndex != PendingInput.PlayerIndex)
                return SubmitResult.Rejected("Not your turn to act");

            if (PendingInput.Kind == PendingInputKind.Decision)
            {
                if (action is not SubmitDecisionAction decision)
                {
                    if (action is not ConcedeAction concede)
                        return SubmitResult.Rejected("A decision is pending");
                    var conceded = Concede(concede.PlayerIndex);
                    return conceded;
                }
                if (decision.Answer == null || decision.Answer.DecisionId != PendingInput.Decision.Id)
                    return SubmitResult.Rejected("Answer does not match the pending decision");
                var error = ValidateAnswer(PendingInput.Decision, decision.Answer);
                if (error != null)
                    return SubmitResult.Rejected(error);

                var request = PendingInput.Decision;
                PendingInput = null;
                Emit(new DecisionMadeEvent { PlayerIndex = action.PlayerIndex, DecisionId = request.Id });
                // These decisions select publicly known effects. Keep their identities
                // on the resolution context while a child effect is awaiting input.
                if (request.Context == "soi.copy" || request.Context == "soi.mode")
                    foreach (int chosenId in decision.Answer.ChosenOptionIds)
                    {
                        int ordinal = request.Options.FindIndex(o => o.Id == chosenId);
                        var option = request.Options[ordinal];
                        _activeContext.PublicSelections.Add(new ShardsPublicSelection
                        { Context = request.Context, DefId = option.DefId, InstanceId = option.CardInstanceId,
                            Label = option.Label, Ordinal = ordinal });
                    }
                // "Choose one" branches are public: announce which one, so the play log
                // can say what an opponent's card actually did.
                if (request.Context == "soi.mode" && decision.Answer.ChosenOptionIds.Count > 0)
                    foreach (var option in request.Options)
                        if (option.Id == decision.Answer.ChosenOptionIds[0])
                        {
                            Emit(new ShardsModeChosenEvent
                            {
                                PlayerIndex = action.PlayerIndex,
                                DefId = _activeContext?.Source?.DefId,
                                Label = option.Label
                            });
                            break;
                        }
                _activeContext.Answer = decision.Answer;
                PumpEffects();
                return SubmitResult.Ok();
            }

            var result = ExecuteTurnAction(action);
            if (result.Accepted)
            {
                Pump();
            }
            return result;
        }

        private static string ValidateAnswer(DecisionRequest request, DecisionAnswer answer)
        {
            if (answer.ChosenOptionIds.Count < request.Min) return "Too few options chosen";
            if (answer.ChosenOptionIds.Count > request.Max) return "Too many options chosen";
            // The damage split assigns one option id PER POINT — duplicates are its
            // mechanism. Every other decision picks distinct options; a duplicated id
            // would double-count (e.g. banishing one card "three times" for triple pay).
            bool allowDuplicates = request.Context == "soi.split";
            var seen = new HashSet<int>();
            foreach (int id in answer.ChosenOptionIds)
            {
                bool known = false;
                foreach (var option in request.Options)
                    if (option.Id == id)
                    {
                        // Shown-but-greyed options (a reveal's non-qualifying cards) are
                        // display only — never a legal pick, however the client asks.
                        if (option.Disabled) return "Option " + id + " is not selectable";
                        known = true;
                    }
                if (!known) return "Unknown option " + id;
                if (!allowDuplicates && !seen.Add(id)) return "Duplicate option " + id;
            }
            return null;
        }

        // ------------------------------------------------------------------ turn actions

        private SubmitResult ExecuteTurnAction(PlayerAction action)
        {
            var player = State.Players[action.PlayerIndex];
            if (player.Eliminated) return SubmitResult.Rejected("You are eliminated");
            if (action.PlayerIndex != State.TurnPlayerIndex)
                return SubmitResult.Rejected("Not your turn");

            switch (action)
            {
                case ShardsPlayCardAction play:
                    return PlayCard(player, play.CardInstanceId);
                case ShardsBuyCardAction buy:
                    return BuyCard(player, buy.SlotIndex, buy.FastPlay);
                case ShardsFocusAction:
                    return Focus(player);
                case ShardsExhaustAction exhaust:
                    return ExhaustCard(player, exhaust.CardInstanceId);
                case ShardsAttackChampionAction champion:
                    return AttackChampion(player, champion.TargetPlayerIndex, champion.CardInstanceId, champion.Amount);
                case ShardsAttackMonsterAction monster:
                    return AttackMonster(player, monster.CardInstanceId, monster.Amount);
                case ShardsTakeDestinyAction destiny:
                    return TakeDestiny(player, destiny.CardInstanceId);
                case ShardsRecruitRelicAction relic:
                    return RecruitRelic(player, relic.CardInstanceId);
                case ShardsRerollRowAction reroll:
                    return RerollRow(player, reroll.SlotIndex);
                case ShardsHeroAbilityAction:
                    return HeroAbility(player);
                case ShardsEndTurnAction:
                    BeginEndTurn(player);
                    return SubmitResult.Ok();
                case ConcedeAction:
                    return Concede(player.Index);
                default:
                    return SubmitResult.Rejected("Unknown action");
            }
        }

        private SubmitResult PlayCard(ShardsPlayer player, int instanceId)
        {
            var card = player.Hand.Find(c => c.InstanceId == instanceId);
            if (card == null) return SubmitResult.Rejected("Card not in hand");
            var def = card.Def;

            player.Hand.Remove(card);
            if (def.IsChampion)
            {
                card.Zone = ShardsZone.Champions;
                card.Exhausted = false;
                card.DamageThisTurn = 0;
                player.Champions.Add(card);
                Emit(new ShardsChampionDeployedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });

                // Praetorian-01: bounces from the DISCARD pile when a champion is played.
                foreach (var relic in player.Discard.FindAll(c => c.Def.ReturnsFromDiscardOnChampionPlay))
                {
                    player.Discard.Remove(relic);
                    relic.Zone = ShardsZone.Hand;
                    player.Hand.Add(relic);
                    Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = relic.InstanceId, DefId = relic.DefId });
                }
            }
            else
            {
                card.Zone = ShardsZone.PlayZone;
                player.PlayZone.Add(card);
            }
            CountPlay(player, card);
            Emit(new ShardsCardPlayedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });

            QueuePlayEffect(player, card);
            return SubmitResult.Ok();
        }

        private SubmitResult BuyCard(ShardsPlayer player, int slotIndex, bool fastPlay)
        {
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length)
                return SubmitResult.Rejected("Invalid slot");
            var card = State.CenterRow[slotIndex];
            if (card == null) return SubmitResult.Rejected("Empty slot");
            var def = card.Def;
            if (fastPlay && def.Type != ShardsCardType.Mercenary)
                return SubmitResult.Rejected("Only mercenaries can be fast-played");
            if (fastPlay && def.CannotBeFastPlayed)
                return SubmitResult.Rejected("That card must be bought");
            int cost = EffectiveCost(player, def);
            if (player.Gems < cost) return SubmitResult.Rejected("Not enough gems");

            if (cost > 0)
            {
                player.Gems -= cost;
                Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -cost, NewValue = player.Gems });
            }
            player.FirstBuyUsedThisTurn = true; // Decima's M5 first-buy discount is now spent

            // Row refills IMMEDIATELY — before the card's effect resolves.
            State.CenterRow[slotIndex] = null;
            RefillSlot(slotIndex);

            Emit(new ShardsCardBoughtEvent
            {
                PlayerIndex = player.Index,
                SlotIndex = slotIndex,
                DefId = card.DefId,
                CostPaid = cost,
                FastPlay = fastPlay
            });

            if (fastPlay)
            {
                card.Owner = player.Index;
                card.Zone = ShardsZone.PlayZone;
                card.FastPlayed = true;
                player.PlayZone.Add(card);
                CountPlay(player, card); // a fast-play counts as playing the card
                QueuePlayEffect(player, card);
            }
            else
            {
                card.Owner = player.Index;
                card.FastPlayed = false;
                RecruitTo(player, card);
            }
            return SubmitResult.Ok();
        }

        /// <summary>Recruited cards normally land in the discard, but turn effects can
        /// redirect them: Numeri Drones (Homodeus champion → directly into play),
        /// Anomaly Cleric M10 (→ hand), Maglev Tunnels (Homodeus champion → deck top).</summary>
        private void RecruitTo(ShardsPlayer player, ShardsCard card)
        {
            NotifyRecruit(player, card);
            var def = card.Def;
            // Deploy a recruited champion directly into play: Numeri Drones (Homodeus only)
            // or Century Forge / Duel (any champion, via the general counter).
            bool deployDirect = def.IsChampion &&
                (player.NextChampionsIntoPlay > 0 ||
                 (player.NextHomodeusChampionsIntoPlay > 0 && def.Faction == ShardsFaction.Homodeus));
            if (deployDirect)
            {
                if (player.NextChampionsIntoPlay > 0) player.NextChampionsIntoPlay--;
                else player.NextHomodeusChampionsIntoPlay--;
                card.Zone = ShardsZone.Champions;
                card.Exhausted = false;
                player.Champions.Add(card);
                Emit(new ShardsChampionDeployedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                return;
            }
            if (player.NextRecruitsToHand > 0 || def.RecruitsToHand)
            {
                if (!def.RecruitsToHand)
                    player.NextRecruitsToHand--;
                card.Zone = ShardsZone.Hand;
                player.Hand.Add(card);
                Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                return;
            }
            card.Zone = ShardsZone.Discard;
            player.Discard.Add(card);
            if (def.IsChampion && def.Faction == ShardsFaction.Homodeus &&
                player.Destinies.Exists(d => d.Def.RedirectChampionRecruitsToDeckTop))
            {
                // Maglev Tunnels: owner MAY move the recruit to the top of their deck.
                QueueEffect(new Custom(ctx => MaglevFlow(ctx, card)), player.Index, card);
            }
        }

        private IEnumerable<ShardsStep> MaglevFlow(ShardsContext ctx, ShardsCard card)
        {
            var request = new DecisionRequest
            {
                Id = State.NextDecisionId++,
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Put {card.Def.Name} on top of your deck?",
                Context = "soi.maglev",
                Min = 0,
                Max = 1
            };
            request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var player = ctx.Controller;
            if (!player.Discard.Remove(card)) yield break; // moved elsewhere meanwhile
            card.Zone = ShardsZone.Deck;
            player.Deck.Add(card); // list end = top
            Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId, ToDeckTop = true });
        }

        private SubmitResult Focus(ShardsPlayer player)
        {
            if (player.FocusedThisTurn) return SubmitResult.Rejected("Already focused this turn");
            if (player.CharacterExhausted) return SubmitResult.Rejected("Character already exhausted");
            if (player.Gems < 1) return SubmitResult.Rejected("Focus costs 1 gem");

            player.Gems -= 1;
            player.CharacterExhausted = true;
            player.FocusedThisTurn = true;
            Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -1, NewValue = player.Gems });
            Emit(new ShardsCharacterExhaustedEvent { PlayerIndex = player.Index, CardInstanceId = -1 });
            Emit(new ShardsFocusedEvent { PlayerIndex = player.Index });
            GainMastery(player.Index, 1);
            return SubmitResult.Ok();
        }

        // ---- Duel of Doom hero abilities (character-keyed, like Focus) ----

        /// <summary>UI-facing description of a hero's unique Duel ability. English strings —
        /// display layers localize via their own dictionaries. Active=false → the ability is
        /// a PASSIVE (Decima) and has no activation button.</summary>
        public readonly struct HeroAbilitySpec
        {
            public readonly string Name;
            public readonly string Text;
            public readonly int Mastery, Gems, Health;
            public readonly bool Active;

            public HeroAbilitySpec(string name, string text, int mastery, int gems, int health, bool active)
            {
                Name = name; Text = text; Mastery = mastery; Gems = gems; Health = health; Active = active;
            }
        }

        /// <summary>Single source of truth for every hero's Duel ability metadata (costs,
        /// names, rules text). The engine's activation path and every UI read this.</summary>
        public const int DecimaFirstBuyDiscount = 2;

        public static HeroAbilitySpec HeroAbilityInfo(string characterId) => characterId switch
        {
            "decima" => new HeroAbilitySpec("Recruiting",
                $"M5 passive: the first card you buy each turn costs {DecimaFirstBuyDiscount} less.", 5, 0, 0, active: false),
            // September 27 balance: two draws now cost three gems.
            "tetra" => new HeroAbilitySpec("Perception",
                "M5, once per turn: pay 3 gems, draw 2 cards.", 5, 3, 0, active: true),
            "volos" => new HeroAbilitySpec("First Aid",
                "M5, once per turn: choose one:\n— Free: gain 3 health.\n— Pay 1 gem: gain 3 power.\n— Pay 2 gems: draw 1 card.\n— Pay 3 gems: gain 1 mastery.", 5, 0, 0, active: true),
            // September 27 balance: thinning costs one health.
            "kosynwu" => new HeroAbilitySpec("Sacrifice",
                "M5, once per turn: pay 1 health, banish a card from your hand or discard pile.", 5, 0, 1, active: true),
            // The passive reroll discount does not require exhausting the ability.
            "rez" => new HeroAbilitySpec("Futureproof",
                "M5 passive: all your rerolls cost 1 gem less.\nM5, once per turn: Scry 3 the center deck.", 5, 0, 0, active: true),
            _ => new HeroAbilitySpec(null, null, 0, 0, 0, active: false)
        };

        /// <summary>The activated effect behind <see cref="HeroAbilityInfo"/> (costs live
        /// there; Decima's passive lives in <see cref="EffectiveCost"/>). Public so the
        /// value model can price it through the ordinary effect walker instead of
        /// carrying a second, hand-written table of what each hero is worth.</summary>
        public static IShardsEffect HeroAbilityEffect(string characterId) => characterId switch
        {
            "tetra" => new Gain { Draw = 2 },
            "volos" => new VolosAbilityChoice(),
            "kosynwu" => new BanishUpTo(1),
            "rez" => new Scry(3),
            _ => null
        };

        /// <summary>True if the player may activate their hero ability right now (Duel only).</summary>
        private bool HeroAbilityAvailable(ShardsPlayer player)
        {
            if ((State.Dlc & ShardsDlc.Duel) == 0 || player.HeroAbilityUsedThisTurn) return false;
            var spec = HeroAbilityInfo(player.CharacterId);
            return spec.Active && player.Mastery >= spec.Mastery &&
                   player.Gems >= spec.Gems && player.Health > spec.Health; // never self-eliminate on the cost
        }

        private SubmitResult HeroAbility(ShardsPlayer player)
        {
            if ((State.Dlc & ShardsDlc.Duel) == 0) return SubmitResult.Rejected("Hero abilities require Duel of Doom");
            if (player.HeroAbilityUsedThisTurn) return SubmitResult.Rejected("Hero ability already used this turn");
            var spec = HeroAbilityInfo(player.CharacterId);
            var effect = HeroAbilityEffect(player.CharacterId);
            if (!spec.Active || effect == null) return SubmitResult.Rejected("Your hero has no activated ability");
            if (player.Mastery < spec.Mastery) return SubmitResult.Rejected($"Requires Mastery {spec.Mastery}");
            if (player.Gems < spec.Gems) return SubmitResult.Rejected($"Costs {spec.Gems} gems");
            if (player.Health <= spec.Health) return SubmitResult.Rejected($"Costs {spec.Health} health");

            if (spec.Gems > 0)
            {
                player.Gems -= spec.Gems;
                Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -spec.Gems, NewValue = player.Gems });
            }
            if (spec.Health > 0) LoseHealth(player.Index, spec.Health);
            player.HeroAbilityUsedThisTurn = true;
            Emit(new ShardsHeroAbilityUsedEvent { PlayerIndex = player.Index, CharacterId = player.CharacterId });
            QueueEffect(effect, player.Index, null);
            return SubmitResult.Ok();
        }

        private SubmitResult ExhaustCard(ShardsPlayer player, int instanceId)
        {
            // Champions and owned Destinies both carry once-per-turn exhaust powers.
            var card = player.Champions.Find(c => c.InstanceId == instanceId)
                       ?? player.Destinies.Find(c => c.InstanceId == instanceId);
            if (card == null) return SubmitResult.Rejected("Card not in play");
            if (card.Exhausted) return SubmitResult.Rejected("Already exhausted this turn");
            var def = card.Def;
            if (def.ExhaustEffect == null) return SubmitResult.Rejected("No exhaust ability");
            if (player.Gems < def.ExhaustGemCost)
                return SubmitResult.Rejected($"Costs {def.ExhaustGemCost} gems to activate");

            if (def.ExhaustGemCost > 0)
            {
                player.Gems -= def.ExhaustGemCost;
                Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -def.ExhaustGemCost, NewValue = player.Gems });
            }
            card.Exhausted = true;
            Emit(new ShardsCharacterExhaustedEvent { PlayerIndex = player.Index, CardInstanceId = card.InstanceId });
            QueueEffect(def.ExhaustEffect, player.Index, card);
            // Unknown God (Duel M20): the owner's exhaust effects resolve a second time.
            if (player.Champions.Exists(c => c.Def.DoublesExhaustsAtMastery >= 0 &&
                                             player.Mastery >= c.Def.DoublesExhaustsAtMastery))
                QueueEffect(def.ExhaustEffect, player.Index, card);
            return SubmitResult.Ok();
        }


        private SubmitResult AttackChampion(ShardsPlayer player, int targetPlayerIndex, int instanceId, int amount)
        {
            if ((State.Dlc & ShardsDlc.Duel) == 0)
                return SubmitResult.Rejected("Champions can only be destroyed in the end-of-turn damage assignment");
            if (targetPlayerIndex < 0 || targetPlayerIndex >= State.Players.Count || targetPlayerIndex == player.Index)
                return SubmitResult.Rejected("Invalid champion owner");
            var owner = State.Players[targetPlayerIndex];
            if (owner.Eliminated) return SubmitResult.Rejected("That player is eliminated");
            var champion = owner.Champions.Find(c => c.InstanceId == instanceId);
            if (champion == null) return SubmitResult.Rejected("No such enemy champion");
            if (!CanAttackChampion(player, owner, champion))
                return SubmitResult.Rejected("That champion cannot be attacked");
            int remaining = System.Math.Max(0, EffectiveDefense(owner, champion) - champion.DamageThisTurn);
            if (amount < 0 || amount != 0 && amount != remaining)
                return SubmitResult.Rejected("Champion attacks require exact remaining defense");
            if (player.Power < remaining) return SubmitResult.Rejected("Not enough power");
            player.Power -= remaining;
            if (remaining > 0)
                Emit(new ShardsPowerChangedEvent { PlayerIndex = player.Index, Delta = -remaining, NewValue = player.Power });
            // Emit the paid hit before destruction so presentation and statistics can
            // distinguish a power attack from a direct-destruction card effect.
            champion.DamageThisTurn += remaining;
            Emit(new ShardsChampionDamagedEvent
            {
                OwnerIndex = owner.Index, ByPlayerIndex = player.Index,
                InstanceId = champion.InstanceId, DefId = champion.DefId,
                Amount = remaining, Total = champion.DamageThisTurn
            });
            // The full cost was validated against live public defense. Destroy now;
            // Submit's pump resolves follow-up effects before offering another action.
            DestroyChampion(owner, champion, player.Index);
            ResolveLethalChampions(player.Index);
            return SubmitResult.Ok();
        }

        private SubmitResult AttackMonster(ShardsPlayer player, int instanceId, int amount)
        {
            if ((State.Dlc & ShardsDlc.IntoTheHorizon) == 0)
                return SubmitResult.Rejected("Monsters are not in this game");
            var monster = State.ActiveMonsters.Find(m => m.InstanceId == instanceId);
            if (monster == null) return SubmitResult.Rejected("No such Ingeminex");
            var def = monster.Def;

            // Same accumulation model as champions (rules: "similar to using Power to
            // defeat an opponent's Champion"); marks evaporate at end of turn.
            int remaining = def.Defense - monster.DamageThisTurn;
            int spend = amount <= 0 ? remaining : System.Math.Min(amount, remaining);
            if (spend <= 0) return SubmitResult.Rejected("Ingeminex already fully damaged");
            if (player.Power < spend) return SubmitResult.Rejected("Not enough power");

            player.Power -= spend;
            Emit(new ShardsPowerChangedEvent { PlayerIndex = player.Index, Delta = -spend, NewValue = player.Power });
            monster.DamageThisTurn += spend;

            if (monster.DamageThisTurn < def.Defense)
            {
                Emit(new ShardsMonsterDamagedEvent
                {
                    PlayerIndex = player.Index,
                    InstanceId = monster.InstanceId,
                    DefId = monster.DefId,
                    Amount = spend,
                    Total = monster.DamageThisTurn
                });
                return SubmitResult.Ok();
            }

            // Defeated: bottom of the center deck (it never occupied a row slot), its
            // pending attack is cancelled, and YOU alone gain the printed reward now.
            State.ActiveMonsters.Remove(monster);
            State.PendingMonsterAttacks.Remove(monster.InstanceId);
            monster.DamageThisTurn = 0;
            monster.Zone = ShardsZone.CenterDeck;
            State.CenterDeck.Insert(0, monster); // list end = top; index 0 = bottom
            Emit(new ShardsMonsterDefeatedEvent { PlayerIndex = player.Index, InstanceId = monster.InstanceId, DefId = monster.DefId });

            if (def.RewardEffect != null)
                QueueEffect(def.RewardEffect, player.Index, monster);
            return SubmitResult.Ok();
        }

        private SubmitResult TakeDestiny(ShardsPlayer player, int instanceId)
        {
            if ((State.Dlc & ShardsDlc.IntoTheHorizon) == 0)
                return SubmitResult.Rejected("Destinies are not in this game");
            if (player.DestinyTaken) return SubmitResult.Rejected("Destiny already taken this game");
            if (player.Mastery < 5) return SubmitResult.Rejected("Requires Mastery 5");
            var destiny = State.DestinyRow.Find(c => c.InstanceId == instanceId);
            if (destiny == null) return SubmitResult.Rejected("Not in the destiny row");

            player.DestinyTaken = true;
            GrantDestiny(player, destiny);
            return SubmitResult.Ok();
        }

        /// <summary>Move a destiny from the row in front of a player (also used by the
        /// Agony/Malice Ingeminex rewards, which bypass Mastery 5 AND the one-per-game
        /// limit — those callers don't set DestinyTaken).</summary>
        public void GrantDestiny(ShardsPlayer player, ShardsCard destiny)
        {
            State.DestinyRow.Remove(destiny); // the row shrinks — never refilled
            destiny.Owner = player.Index;
            destiny.Zone = ShardsZone.SetAside; // owned destinies sit in front of the player
            destiny.Exhausted = false;
            player.Destinies.Add(destiny);
            Emit(new ShardsDestinyTakenEvent { PlayerIndex = player.Index, InstanceId = destiny.InstanceId, DefId = destiny.DefId });
            if (destiny.Def.PlayEffect != null)
                QueueEffect(destiny.Def.PlayEffect, player.Index, destiny);
        }

        private SubmitResult RecruitRelic(ShardsPlayer player, int instanceId)
        {
            if (player.RelicRecruited) return SubmitResult.Rejected("Relic already recruited this game");
            if (player.Mastery < 10) return SubmitResult.Rejected("Requires Mastery 10");
            var relic = player.SetAside.Find(c => c.InstanceId == instanceId && c.Def.Type == ShardsCardType.Relic);
            if (relic == null) return SubmitResult.Rejected("Not one of your relics");

            player.RelicRecruited = true;
            // Only the chosen relic leaves set-aside: the other stays there, normally dead
            // weight — but the Ingeminex Corruption reward can still fetch it (ItH).
            player.SetAside.Remove(relic);
            relic.Zone = ShardsZone.Discard;
            player.Discard.Add(relic);
            Emit(new ShardsRelicRecruitedEvent { PlayerIndex = player.Index, DefId = relic.DefId });
            NotifyRecruit(player, relic);
            return SubmitResult.Ok();
        }

        /// <summary>Duel of Doom row reroll price: 1 gem for the first reroll of the turn,
        /// +1 per subsequent reroll (1, 2, 3…), resetting each turn — the first look is
        /// nearly free, but digging the whole shop for one card gets expensive fast.
        /// Rez at mastery 5 discounts EVERY reroll by one, independently of activation.
        /// A def may opt out entirely (Comet).</summary>
        public static int RerollDiscount(ShardsPlayer player) => player.NextRerollDiscount
            + (player.CharacterId == "rez" && player.Mastery >= 5 ? 1 : 0);

        public static int RerollCost(ShardsPlayer player) =>
            System.Math.Max(0, 1 + player.RerollsThisTurn - RerollDiscount(player));

        private SubmitResult RerollRow(ShardsPlayer player, int slotIndex)
        {
            if ((State.Dlc & ShardsDlc.Duel) == 0) return SubmitResult.Rejected("Row reroll requires Duel of Doom");
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length) return SubmitResult.Rejected("Invalid slot");
            var card = State.CenterRow[slotIndex];
            if (card == null) return SubmitResult.Rejected("Empty slot");
            if (card.Def.CannotBeRerolled) return SubmitResult.Rejected("That card can't be removed from the shop");
            int cost = RerollCost(player);
            if (player.Gems < cost) return SubmitResult.Rejected($"Reroll costs {cost} gems right now");

            player.Gems -= cost;
            player.RerollsThisTurn++;
            player.NextRerollDiscount = 0;
            Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -cost, NewValue = player.Gems });
            Emit(new ShardsRowRerolledEvent { PlayerIndex = player.Index, SlotIndex = slotIndex, DefId = card.DefId });
            BottomRowCardAndRefill(slotIndex);
            return SubmitResult.Ok();
        }

        /// <summary>Send a center-row card to the bottom of the center deck and refill the
        /// slot. Used by the paid row reroll AND free "remove a card from the shop" effects
        /// (Order Initiate errata).</summary>
        public void BottomRowCardAndRefill(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length) return;
            var card = State.CenterRow[slotIndex];
            if (card == null) return;
            State.CenterRow[slotIndex] = null;
            card.Zone = ShardsZone.CenterDeck;
            State.CenterDeck.Insert(0, card);
            Emit(new ShardsCenterCardBottomedEvent { InstanceId = card.InstanceId, DefId = card.DefId });
            RefillSlot(slotIndex);
        }

        /// <summary>A card's shield value for this owner: dynamic overrides (Datic Robes =
        /// mastery, Praetorian-02 M20) plus Phasic Technology (+2 on Homodeus/Order cards).</summary>
        public int ShieldValue(ShardsPlayer owner, ShardsCard card)
        {
            var def = card.Def;
            int value = def.DynamicShield != null ? def.DynamicShield(owner) : def.Shield;
            if ((def.Faction == ShardsFaction.Homodeus || def.Faction == ShardsFaction.Order) &&
                owner.Destinies.Exists(d => d.DefId == "phasic_technology"))
                value += 2;
            // Praetorian-02 (Duel errata): shields doubled until the owner's next turn.
            if (owner.ShieldsDoubledUntilNextTurn && value > 0)
                value *= 2;
            return value;
        }

        /// <summary>Faction identity check honoring Project Yggdrasil (the owner's Wraethe
        /// cards also count as Undergrowth and vice versa).</summary>
        public static bool CountsAs(ShardsPlayer owner, ShardsCardDef def, ShardsFaction faction)
        {
            if (def.Faction == faction) return true;
            // Prism (Duel): counts as a card of every real faction.
            if (def.CountsAsEveryFaction && faction != ShardsFaction.None && faction != ShardsFaction.Monster)
                return true;
            if (!owner.Destinies.Exists(d => d.DefId == "project_yggdrasil")) return false;
            return (faction == ShardsFaction.Wraethe && def.Faction == ShardsFaction.Undergrowth) ||
                   (faction == ShardsFaction.Undergrowth && def.Faction == ShardsFaction.Wraethe);
        }

        /// <summary>Count a play for faction triggers; Project Yggdrasil double-counts
        /// Wraethe/Undergrowth plays as each other. Also fires discard-pile play triggers
        /// (The Dispossessed).</summary>
        private void CountPlay(ShardsPlayer player, ShardsCard card)
        {
            var def = card.Def;
            bool isAlly = !def.IsChampion;
            if (def.CountsAsEveryFaction)
            {
                // Prism (Duel): a play of every real faction at once.
                player.CountFactionPlay(ShardsFaction.Homodeus, isAlly);
                player.CountFactionPlay(ShardsFaction.Undergrowth, isAlly);
                player.CountFactionPlay(ShardsFaction.Order, isAlly);
                player.CountFactionPlay(ShardsFaction.Wraethe, isAlly);
                player.CountFactionPlay(ShardsFaction.Aion, isAlly);
            }
            else
            {
                player.CountFactionPlay(def.Faction, isAlly);
                if (player.Destinies.Exists(d => d.DefId == "project_yggdrasil"))
                {
                    if (def.Faction == ShardsFaction.Wraethe)
                        player.CountFactionPlay(ShardsFaction.Undergrowth, isAlly);
                    else if (def.Faction == ShardsFaction.Undergrowth)
                        player.CountFactionPlay(ShardsFaction.Wraethe, isAlly);
                }
            }
            player.PlayedThisTurn.Add(card);

            // An actual play, including Warp and fast-play, is the trigger. Copying
            // effects and revealing hand shields never call CountPlay. Use the live
            // shield value, so Phasic Technology and dynamic shields qualify too.
            if (ShieldValue(player, card) > 0)
            {
                int bonus = 0;
                foreach (var source in player.Champions)
                    bonus += source.Def.ChampionDefensePerShieldPlay;
                if (bonus > 0)
                    foreach (var champion in player.Champions)
                        champion.TemporaryDefenseUntilNextTurn += bonus;
            }

            // The Dispossessed: a matching-faction play lets it return from the discard.
            foreach (var waiting in player.Discard.FindAll(c =>
                         c.Def.ReturnFromDiscardOnFactionPlay != ShardsFaction.None &&
                         CountsAs(player, def, c.Def.ReturnFromDiscardOnFactionPlay) &&
                         c != card))
            {
                var captured = waiting;
                QueueEffect(new Custom(ctx => OptionalReturnFlow(ctx, captured)), player.Index, captured);
            }
        }

        private IEnumerable<ShardsStep> OptionalReturnFlow(ShardsContext ctx, ShardsCard card)
        {
            var player = ctx.Controller;
            if (!player.Discard.Contains(card)) yield break;
            var request = new DecisionRequest
            {
                Id = State.NextDecisionId++,
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = $"Return {card.Def.Name} from your discard pile to your hand?",
                Context = "soi.return",
                Min = 0,
                Max = 1
            };
            request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            if (!player.Discard.Remove(card)) yield break;
            card.Zone = ShardsZone.Hand;
            player.Hand.Add(card);
            Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
        }

        /// <summary>Row price after modifiers (Axia: cheaper per Homodeus champion in play).</summary>
        public int EffectiveCost(ShardsPlayer buyer, ShardsCardDef def)
        {
            int cost = def.Cost;
            if (def.CostModifier != null)
                cost += def.CostModifier(buyer);
            // Decima (Duel) M5 passive: the first card you buy each turn costs 2 less.
            if ((State.Dlc & ShardsDlc.Duel) != 0 && buyer.CharacterId == "decima" &&
                buyer.Mastery >= 5 && !buyer.FirstBuyUsedThisTurn)
                cost -= DecimaFirstBuyDiscount;
            return System.Math.Max(0, cost);
        }

        /// <summary>Printed defense plus auras from the owner's champions and destinies
        /// (Ferrata Guard, One Mind One Army).</summary>
        public int EffectiveDefense(ShardsPlayer owner, ShardsCard champion)
        {
            int defense = champion.Def.Defense + champion.TemporaryDefenseUntilNextTurn;
            foreach (var source in owner.Champions)
                if (source.Def.DefenseAura != null)
                    defense += source.Def.DefenseAura(owner, source, champion);
            foreach (var source in owner.Destinies)
                if (source.Def.DefenseAura != null)
                    defense += source.Def.DefenseAura(owner, source, champion);
            return defense;
        }

        /// <summary>Targeting rules: a Taunt champion (Zetta) shields its owner's OTHER
        /// champions; per-card vetoes (Li Hin, Raidian, Drakonarius) apply on top.</summary>
        public bool CanAttackChampion(ShardsPlayer attacker, ShardsPlayer owner, ShardsCard champion)
        {
            foreach (var other in owner.Champions)
                if (other != champion && other.Def.Taunt &&
                    ((State.Dlc & ShardsDlc.Duel) == 0 || !champion.Def.Taunt))
                    return false;
            var veto = champion.Def.CanBeAttacked;
            return veto == null || veto(State, attacker, owner, champion);
        }

        /// <summary>Zetta also protects the PLAYER: end-turn damage can't be assigned to
        /// an opponent with a Taunt champion in play.</summary>
        public bool CanAssignDamageTo(ShardsPlayer defender)
        {
            foreach (var champion in defender.Champions)
                if (champion.Def.Taunt)
                    return false;
            return true;
        }

        private SubmitResult Concede(int playerIndex)
        {
            var player = State.Players[playerIndex];
            if (player.Eliminated) return SubmitResult.Rejected("Already out");
            Emit(new ShardsConcededEvent { PlayerIndex = playerIndex });
            EliminatePlayer(player);
            CheckStateBased();

            // If the conceder owed a decision, answer it minimally so the parked effect
            // iterator can finish instead of stalling the game.
            if (!State.GameOver && PendingInput != null &&
                PendingInput.Kind == PendingInputKind.Decision &&
                PendingInput.Decision.PlayerIndex == playerIndex &&
                _activeContext != null)
            {
                var request = PendingInput.Decision;
                var answer = new DecisionAnswer { DecisionId = request.Id };
                for (int i = 0; i < request.Min && i < request.Options.Count; i++)
                    answer.ChosenOptionIds.Add(request.Options[i].Id);
                PendingInput = null;
                Emit(new DecisionMadeEvent { PlayerIndex = playerIndex, DecisionId = request.Id });
                _activeContext.Answer = answer;
                PumpEffects();
            }

            if (!State.GameOver && State.TurnPlayerIndex == playerIndex && !_endTurnInProgress &&
                (PendingInput == null || PendingInput.Kind != PendingInputKind.Decision))
                AdvanceTurn();
            RoutePriority();
            return SubmitResult.Ok();
        }

        // ------------------------------------------------------------------ end turn

        private const int AutomaticCombatPowerThreshold = 1000;

        /// <summary>Whether the existing overwhelming-power shortcut will immediately
        /// win on end turn. Uses public state only, so clients can omit a redundant
        /// champion reminder without inspecting hands. Infinite power bypasses guards.</summary>
        public bool WouldEndTurnWinAutomatically(ShardsPlayer player)
        {
            if (State.GameOver || player == null || player.Eliminated ||
                player.Power <= AutomaticCombatPowerThreshold) return false;
            bool hasOpponent = false;
            foreach (var opponent in State.LivingOpponentsOf(player.Index))
            {
                hasOpponent = true;
                if (opponent.Health > player.Power) return false;
            }
            return hasOpponent;
        }

        private void BeginEndTurn(ShardsPlayer player)
        {
            _endTurnInProgress = true;

            var living = new List<ShardsPlayer>(State.LivingOpponentsOf(player.Index));

            // Overwhelming power (the infinite Infinity Shard): no split window —
            // bypass Zetta and resolve against every opponent automatically.
            // Shields cap out around 30; against 1000+ they cannot matter,
            // so no reveal prompts either.
            if (player.Power > AutomaticCombatPowerThreshold && living.Count > 0)
            {
                foreach (var opponent in living)
                    ApplyDamage(player.Index, opponent, player.Power, 0, revealed: null);
                AfterDefenses(player);
                return;
            }

            var assignable = new List<ShardsPlayer>(living);
            assignable.RemoveAll(o => !CanAssignDamageTo(o)); // Taunt (Zetta) protects its owner

            // Enemy champions share the power pool (marks accumulate and reset at end
            // of turn exactly like mid-turn attacks). Per-card vetoes (Li Hin…) always
            // exclude; the TAUNT block is expressed as a Required option instead —
            // the split may reach the owner's other targets ONLY by killing the taunt
            // champion in the same answer (validated in SplitDamageFlow).
            var championTargets = new List<(ShardsPlayer owner, ShardsCard champion)>();
            if ((State.Dlc & ShardsDlc.Duel) == 0)
            foreach (var championOwner in living)
                foreach (var champion in championOwner.Champions)
                {
                    var veto = champion.Def.CanBeAttacked;
                    if (veto == null || veto(State, player, championOwner, champion))
                        championTargets.Add((championOwner, champion));
                }

            if (player.Power > 0 && (assignable.Count > 0 || championTargets.Count > 0))
            {
                if (assignable.Count == 1 && championTargets.Count == 0)
                {
                    // Only one possible target — skip the split decision.
                    _splitTargets = new List<int> { assignable[0].Index };
                    _splitAmounts = new List<int> { player.Power };
                    BeginDefenses(player);
                    return;
                }

                var request = new DecisionRequest
                {
                    Id = State.NextDecisionId++,
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseMode,
                    Title = $"Assign {player.Power} damage between your opponents",
                    Context = "soi.split",
                    // Full assignment among PLAYERS is mandatory (rulebook: assign
                    // ALL remaining power) as long as someone is directly assignable;
                    // with every player behind a taunt, waste is allowed.
                    Min = assignable.Count > 0 ? player.Power : 0,
                    Max = player.Power,
                    Ordered = true
                };
                foreach (var opponent in living)
                    if ((State.Dlc & ShardsDlc.Duel) == 0 || CanAssignDamageTo(opponent))
                        request.Options.Add(new DecisionOption(opponent.Index, opponent.Name)
                        { OwnerIndex = opponent.Index });
                foreach (var (championOwner, champion) in championTargets)
                    request.Options.Add(new DecisionOption(ChampionSplitBase + champion.InstanceId,
                        champion.Def.Name + " (" + championOwner.Name + ")")
                    {
                        CardInstanceId = champion.InstanceId,
                        DefId = champion.DefId,
                        OwnerIndex = championOwner.Index,
                        Amount = System.Math.Max(1,
                            EffectiveDefense(championOwner, champion) - champion.DamageThisTurn),
                        Required = champion.Def.Taunt
                    });
                // Defaults pad with DISTINCT options only — pre-fill a full assignment
                // (everything on the first ASSIGNABLE opponent) so timeouts and
                // timeout defaults stay legal. With everyone taunt-protected the default
                // is deliberately EMPTY (waste): a timed-out player should not be
                // volunteered into killing champions.
                if (assignable.Count > 0)
                    for (int i = 0; i < player.Power; i++)
                        request.DefaultOptionIds.Add(assignable[0].Index);
                // Answer format: one option id per damage point (repeats allowed).
                // Queue only — Submit's pump picks it up. NEVER pump from inside the
                // end-turn chain: these methods also run inside effect iterators, and a
                // nested pump would clobber the parked iterator (hard-won).
                QueueEffect(new Custom(ctx => SplitDamageFlow(ctx, request)), player.Index, null);
                return;
            }

            AfterDefenses(player);
        }

        private IEnumerable<ShardsStep> SplitDamageFlow(ShardsContext ctx, DecisionRequest request)
        {
            yield return ShardsStep.AwaitDecision(request);

            _splitTargets = new List<int>();
            _splitAmounts = new List<int>();
            var championHits = new Dictionary<int, int>();
            foreach (int optionId in ctx.Answer.ChosenOptionIds)
            {
                if (optionId >= ChampionSplitBase)
                {
                    int hitId = optionId - ChampionSplitBase;
                    championHits.TryGetValue(hitId, out int soFar);
                    championHits[hitId] = soFar + 1;
                    continue;
                }
                int index = _splitTargets.IndexOf(optionId);
                if (index < 0)
                {
                    _splitTargets.Add(optionId);
                    _splitAmounts.Add(1);
                }
                else
                {
                    _splitAmounts[index]++;
                }
            }

            // Taunt (Zetta): assignments to a protected owner — the player itself or
            // any of their OTHER champions — only count when the taunt champion
            // receives LETHAL in this same answer; otherwise those points are dropped
            // (wasted). The UI enforces this up front; this guards the rule against
            // ill-behaved clients.
            foreach (var defender in State.Players)
            {
                var taunt = defender.Champions.Find(c => c.Def.Taunt);
                if (taunt == null || defender.Eliminated) continue;
                championHits.TryGetValue(taunt.InstanceId, out int assigned);
                if (assigned >= EffectiveDefense(defender, taunt) - taunt.DamageThisTurn)
                    continue; // the taunt champion dies — everything behind it unlocks
                int index = _splitTargets.IndexOf(defender.Index);
                if (index >= 0)
                {
                    _splitTargets.RemoveAt(index);
                    _splitAmounts.RemoveAt(index);
                }
                foreach (var other in defender.Champions)
                    if (other != taunt)
                        championHits.Remove(other.InstanceId);
            }

            // Champion damage lands first (shields never protect champions) — EXCEPT for
            // owners with a ShieldsProtectChampions champion in play (Testudo Vanguard,
            // Duel): their champion hits are DEFERRED into their defense step so the
            // shields they reveal reduce each champion's damage individually.
            _pendingChampionHits = new Dictionary<int, List<(int hitId, int amount)>>();
            foreach (var hit in championHits)
            {
                foreach (var championOwner in State.Players)
                {
                    var champion = championOwner.Champions.Find(c => c.InstanceId == hit.Key);
                    if (champion == null) continue;
                    if (championOwner.Champions.Exists(c => c.Def.ShieldsProtectChampions))
                    {
                        if (!_pendingChampionHits.TryGetValue(championOwner.Index, out var list))
                            _pendingChampionHits[championOwner.Index] = list = new List<(int, int)>();
                        list.Add((hit.Key, hit.Value));
                    }
                    else
                    {
                        ApplyPowerToChampion(ctx.ControllerIndex, championOwner, champion, hit.Value);
                    }
                    break;
                }
            }

            BeginDefenses(ctx.Controller);
        }

        /// <summary>Resolve one defender's damage after their shield reveal, in rules
        /// order. Testudo Vanguard (Duel) defers champion hits to here so shields reduce
        /// EACH hit individually (the attacker may have over-assigned to pay through).
        /// Taunt (Zetta) × Testudo: the taunt champion's hit resolves FIRST — if it
        /// SURVIVES its shield-reduced damage, the wall held: every other champion hit
        /// AND the face damage resolve as ZERO.</summary>
        private void ResolveDefenderDamage(int attackerIndex, ShardsPlayer defender,
            int faceAmount, int prevented, List<string> revealed)
        {
            bool tauntHeld = false;
            if (_pendingChampionHits != null &&
                _pendingChampionHits.TryGetValue(defender.Index, out var hits))
            {
                _pendingChampionHits.Remove(defender.Index);
                // Taunt hits first — their survival gates everything behind them.
                hits.Sort((a, b) => IsTauntHit(defender, b.hitId).CompareTo(IsTauntHit(defender, a.hitId)));
                foreach (var (hitId, amount) in hits)
                {
                    if (tauntHeld) continue; // the wall held — nothing reaches past it
                    var champion = defender.Champions.Find(c => c.InstanceId == hitId);
                    if (champion == null) continue;
                    bool isTaunt = champion.Def.Taunt;
                    int dealt = amount - prevented;
                    if (dealt > 0)
                        ApplyPowerToChampion(attackerIndex, defender, champion, dealt);
                    if (isTaunt && defender.Champions.Contains(champion))
                        tauntHeld = true; // survived the shield-reduced hit
                }
            }
            if (!tauntHeld)
                ApplyDamage(attackerIndex, defender, faceAmount, prevented, revealed);
        }

        private static bool IsTauntHit(ShardsPlayer defender, int instanceId)
        {
            var champion = defender.Champions.Find(c => c.InstanceId == instanceId);
            return champion != null && champion.Def.Taunt;
        }

        private void BeginDefenses(ShardsPlayer attacker)
        {
            // Defenders resolve in clockwise turn order from the attacker. The rulebook
            // gives no ordering; each shield decision is independent, so this is purely
            // presentational (rules-notes TODO-VERIFY #1 — outcome-equivalent).
            _pendingDefenses = new Queue<(int, int)>();
            for (int step = 1; step < State.Players.Count; step++)
            {
                int seat = (attacker.Index + step) % State.Players.Count;
                int i = _splitTargets.IndexOf(seat);
                int face = i >= 0 ? _splitAmounts[i] : 0;
                // Deferred champion hits (Testudo) need a defense step even at 0 face
                // damage — the owner's shields resolve against the champion hits there.
                bool championHits = _pendingChampionHits != null &&
                                    _pendingChampionHits.ContainsKey(seat);
                if (face > 0 || championHits)
                    _pendingDefenses.Enqueue((seat, face));
            }
            NextDefense(attacker);
        }

        private void NextDefense(ShardsPlayer attacker)
        {
            while (_pendingDefenses != null && _pendingDefenses.Count > 0)
            {
                var (defenderIndex, amount) = _pendingDefenses.Dequeue();
                var defender = State.Players[defenderIndex];
                if (defender.Eliminated) continue;

                // Ru Bo Vai M10: the attacker ignores ALL shields this turn.
                if (attacker.IgnoreShieldsThisTurn)
                {
                    ResolveDefenderDamage(attacker.Index, defender, amount, 0, revealed: null);
                    continue;
                }

                // A champion's printed shield is INERT while in play (base game) — shields
                // are revealed FROM HAND only. Exception: cards flagged ShieldInPlay
                // (Praetorian-02) shield passively in play and NOT from hand.
                int passive = 0;
                foreach (var champion in defender.Champions)
                    if (champion.Def.ShieldInPlay)
                        passive += ShieldValue(defender, champion);
                // Datic Robes (Duel M20): passive shield while the relic is in the discard.
                foreach (var card in defender.Discard)
                    if (card.Def.DiscardPassiveShield != null)
                        passive += card.Def.DiscardPassiveShield(defender);

                var handShields = defender.Hand.FindAll(c => ShieldValue(defender, c) > 0 && !c.Def.ShieldInPlay);
                if (handShields.Count == 0)
                {
                    ResolveDefenderDamage(attacker.Index, defender, amount, passive, revealed: null);
                    continue;
                }

                var request = new DecisionRequest
                {
                    Id = State.NextDecisionId++,
                    PlayerIndex = defender.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"{attacker.Name} assigns {amount} damage — reveal shields?",
                    Context = "soi.shields",
                    Min = 0,
                    Max = handShields.Count
                };
                foreach (var shield in handShields)
                {
                    var option = new DecisionOption(shield.InstanceId, shield.Def.Name + " (shield " + ShieldValue(defender, shield) + ")")
                    { CardInstanceId = shield.InstanceId, DefId = shield.DefId };
                    request.Options.Add(option);
                }

                int capturedAmount = amount;
                int capturedPassive = passive;
                var capturedDefender = defender;
                // Queue only (see BeginEndTurn) — NextDefense also runs from inside
                // ShieldFlow iterators, where a nested pump would corrupt the pump state.
                QueueEffect(new Custom(ctx => ShieldFlow(ctx, request, attacker.Index, capturedDefender, capturedAmount, capturedPassive)),
                    defender.Index, null);
                return; // resumes via the effect; remaining defenders follow after it
            }

            _pendingDefenses = null;
            AfterDefenses(State.Players[attacker.Index]);
        }

        private IEnumerable<ShardsStep> ShieldFlow(ShardsContext ctx, DecisionRequest request,
            int attackerIndex, ShardsPlayer defender, int amount, int passive)
        {
            yield return ShardsStep.AwaitDecision(request);

            int prevented = 0;
            var revealed = new List<string>();
            var revealedHandIds = new List<int>();
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var card = defender.Hand.Find(c => c.InstanceId == id);
                if (card == null) continue;
                prevented += ShieldValue(defender, card);
                revealed.Add(card.DefId); // shields STAY in hand — reveal only
                revealedHandIds.Add(card.InstanceId);
            }
            if (revealed.Count > 0)
                Emit(new ShardsShieldsRevealedEvent { PlayerIndex = defender.Index, DefIds = revealed, HandInstanceIds = revealedHandIds, Prevented = prevented });

            ResolveDefenderDamage(attackerIndex, defender, amount, passive + prevented, revealed);
            NextDefense(State.Players[attackerIndex]);
        }

        private void ApplyDamage(int attackerIndex, ShardsPlayer defender, int amount, int prevented, List<string> revealed)
        {
            int dealt = amount - prevented;
            if (dealt <= 0) return;
            defender.Health -= dealt;
            Emit(new ShardsHealthChangedEvent { PlayerIndex = defender.Index, Delta = -dealt, NewValue = defender.Health });
            Emit(new ShardsDamageAssignedEvent
            {
                FromPlayerIndex = attackerIndex,
                Targets = new List<int> { defender.Index },
                Amounts = new List<int> { dealt }
            });

            // Unprevented-damage bookkeeping: Heart of Nothing (10+ on one opponent) and
            // owned-destiny triggers (Blood for Blood at 5+).
            var attacker = State.Players[attackerIndex];
            if (dealt > attacker.MaxDamageDealtToOneOpponent)
                attacker.MaxDamageDealtToOneOpponent = dealt;
            foreach (var destiny in attacker.Destinies)
                if (destiny.Def.OnDamageDealt != null)
                {
                    var effect = destiny.Def.OnDamageDealt(dealt);
                    if (effect != null)
                        QueueEffect(effect, attackerIndex, destiny);
                }

            if (defender.Health <= 0)
                EliminatePlayer(defender);
        }

        /// <summary>After all player-damage defenses: Ingeminex revealed this turn attack
        /// ALL players once (ItH), then cleanup runs. Monster effects may pause on
        /// decisions, so cleanup is queued behind them as a final effect.</summary>
        private void AfterDefenses(ShardsPlayer player)
        {
            _pendingDefenses = null;
            _pendingChampionHits = null; // eliminated defenders' deferred hits die with them
            CheckStateBased();
            if (State.GameOver) return;

            bool queued = false;

            // Swyft (while in play, character matches): fast-played cards may be KEPT
            // (recruited to discard) instead of returning to the center deck.
            bool canKeep = player.Champions.Exists(c =>
                (c.Def.KeepFastPlaysCharacter != null && c.Def.KeepFastPlaysCharacter == player.CharacterId) ||
                (c.Def.KeepFastPlaysAtMastery >= 0 && player.Mastery >= c.Def.KeepFastPlaysAtMastery));
            if (canKeep && player.PlayZone.Exists(c => c.FastPlayed))
            {
                QueueEffect(new Custom(ctx => KeepFastPlaysFlow(ctx)), player.Index, null);
                queued = true;
            }

            // Ingeminex attacks moved to FinishEndTurn: they fire AFTER the active
            // player redraws (locked 2026-07-21) so discard attacks hit the fresh hand.

            // Cleanup must run AFTER every effect the damage step queued — not only the
            // ones queued right here. ApplyDamage queues owned-destiny triggers (Blood
            // for Blood); finishing synchronously would discard the play zone before
            // that trigger resolves, silently emptying its candidate list (hard-won:
            // this exact bug shipped once).
            if (queued || _effectQueue.Count > 0)
            {
                // Queue only — no nested pump (see BeginEndTurn).
                QueueEffect(new Custom(_ => FinishFlow(player)), player.Index, null);
                return;
            }

            FinishEndTurn(player);
        }

        private IEnumerable<ShardsStep> KeepFastPlaysFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var fastPlays = player.PlayZone.FindAll(c => c.FastPlayed);
            if (fastPlays.Count == 0) yield break;
            var request = new DecisionRequest
            {
                Id = State.NextDecisionId++,
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Keep fast-played cards? (they join your discard pile)",
                Context = "soi.keepfast",
                Min = 0,
                Max = fastPlays.Count
            };
            foreach (var card in fastPlays)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var card = fastPlays.Find(c => c.InstanceId == id);
                if (card != null)
                    card.FastPlayed = false; // cleanup now treats it as recruited
            }
        }

        private IEnumerable<ShardsStep> FinishFlow(ShardsPlayer player)
        {
            FinishEndTurn(player);
            yield break;
        }

        private void FinishEndTurn(ShardsPlayer player)
        {
            // _endTurnInProgress stays true until AdvanceAfterEndTurn: the Ingeminex
            // attacks queued below may park on decisions, and Concede/RoutePriority
            // must not advance the turn out from under them.
            _splitTargets = null;
            _splitAmounts = null;

            // End phase, in rules order:
            // 1. explicit banishes, otherwise temporary returns, resolve for played cards
            // 2. remaining play-zone cards → discard
            foreach (var card in player.PlayZone)
            {
                if (card.BanishAtCleanup)
                {
                    // Reactor Drone (Duel) mode 2: "banish this card at the end of your turn".
                    // The explicit banish also applies to a temporary fast-play;
                    // it must not return with a stale flag to another player.
                    card.BanishAtCleanup = false;
                    card.FastPlayed = false;
                    card.Zone = ShardsZone.Banished;
                    State.Banished.Add(card);
                    player.CardsBanishedThisTurn++; // still this player's turn
                    Emit(new ShardsCardBanishedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                }
                else if (card.FastPlayed)
                {
                    card.FastPlayed = false;
                    card.Owner = -1;
                    card.Zone = ShardsZone.CenterDeck;
                    State.CenterDeck.Insert(0, card); // list end = top; index 0 = bottom
                    Emit(new ShardsMercenaryReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                }
                else
                {
                    card.Zone = ShardsZone.Discard;
                    player.Discard.Add(card);
                }
            }
            player.PlayZone.Clear();

            // 3. discard the remaining hand
            foreach (var card in new List<ShardsCard>(player.Hand))
            {
                player.Hand.Remove(card);
                card.Zone = ShardsZone.Discard;
                player.Discard.Add(card);
            }

            // 4. ready your champions, destinies and character card; all champion and
            //    Ingeminex damage marks evaporate (they never persist between turns).
            player.CharacterExhausted = false;
            foreach (var champion in player.Champions) champion.Exhausted = false;
            foreach (var destiny in player.Destinies) destiny.Exhausted = false;
            foreach (var p in State.Players)
                foreach (var champion in p.Champions)
                    champion.DamageThisTurn = 0;
            foreach (var monster in State.ActiveMonsters)
                monster.DamageThisTurn = 0;

            // 5. draw a new hand (Heart of Nothing: +N extra if 10+ unprevented damage
            //    landed on a single opponent this turn)
            int toDraw = State.Rules.HandSize;
            if (player.BonusDrawsOnBigHit > 0 && player.MaxDamageDealtToOneOpponent >= 10)
                toDraw += player.BonusDrawsOnBigHit;
            for (int i = 0; i < toDraw; i++)
                DrawOne(player);

            player.ResetTurn();
            Emit(new ShardsCleanupEvent { PlayerIndex = player.Index, RedrawCount = player.Hand.Count });

            // 6. Ingeminex revealed this turn attack — AFTER the redraw (locked
            //    2026-07-21: Agony's discard hits the active player's fresh hand).
            //    Queue only (this runs inside FinishFlow's iterator — pump gotchas),
            //    and park the turn-advance BEHIND the attack effects so their
            //    decisions resolve under the correct TurnPlayerIndex and Round.
            bool queued = QueueMonsterAttacks(player);
            if (queued || _effectQueue.Count > 0)
            {
                QueueEffect(new Custom(_ => AdvanceFlow(player)), player.Index, null);
                return;
            }
            AdvanceAfterEndTurn(player);
        }

        private IEnumerable<ShardsStep> AdvanceFlow(ShardsPlayer player)
        {
            AdvanceAfterEndTurn(player);
            yield break;
        }

        /// <summary>True while an Ingeminex attack effect is resolving — Doom Gate's owner
        /// is skipped by the AllPlayers* attack effects during this window. Transient (only
        /// ever true mid-resolution, false at every quiescent fork point) so it is NOT
        /// cloned or hashed.</summary>
        public bool MonsterAttackActive { get; private set; }

        /// <summary>Doom Gate (Duel): the player is immune to the Ingeminex attack in progress.</summary>
        public bool IsImmuneToActiveMonster(ShardsPlayer player) =>
            MonsterAttackActive && player.Champions.Exists(c => c.Def.ImmuneToIngeminex);

        /// <summary>Emit + queue the attack of every Ingeminex revealed this turn.</summary>
        private bool QueueMonsterAttacks(ShardsPlayer player)
        {
            if (State.PendingMonsterAttacks.Count == 0) return false;
            foreach (int id in new List<int>(State.PendingMonsterAttacks))
            {
                var monster = State.ActiveMonsters.Find(m => m.InstanceId == id);
                if (monster == null) continue;
                Emit(new ShardsMonsterAttackedEvent { InstanceId = monster.InstanceId, DefId = monster.DefId });
                if (monster.Def.MonsterAttackEffect != null)
                {
                    var inner = monster.Def.MonsterAttackEffect;
                    QueueEffect(new Custom(ctx => MonsterAttackWrapper(ctx, inner)), player.Index, monster);
                }
            }
            State.PendingMonsterAttacks.Clear();
            return true;
        }

        private IEnumerable<ShardsStep> MonsterAttackWrapper(ShardsContext ctx, IShardsEffect inner)
        {
            MonsterAttackActive = true;
            foreach (var step in inner.Resolve(ctx))
                yield return step;
            MonsterAttackActive = false;
        }

        /// <summary>Doom Gate (Duel): add N Ingeminex to the center deck and reshuffle
        /// (cycling through the five types). Their attacks fire only when later revealed.</summary>
        public void ShuffleIngeminexIntoCenterDeck(int n)
        {
            var types = new List<string>();
            var replaced = ReplacedIds(State.Dlc);
            foreach (var def in ShardsCardDatabase.All)
                if (def.IsMonster && InInitialCenterPool(def, State.Dlc, replaced))
                    types.Add(def.Id);
            types.Sort(System.StringComparer.Ordinal); // deterministic order
            if (types.Count == 0) return;
            for (int i = 0; i < n; i++)
            {
                string id = types[i % types.Count];
                State.CenterDeck.Add(NewCard(id, -1, ShardsZone.CenterDeck));
                State.GeneratedCardCounts.TryGetValue(id, out int generated);
                State.GeneratedCardCounts[id] = generated + 1;
            }
            State.Rng.Shuffle(State.CenterDeck);
            Emit(new ShardsCenterDeckShuffledEvent());
        }

        /// <summary>Doom Gate (Duel): remove a revealed Ingeminex from play (to the bottom
        /// of the center deck, no reward) and cancel its pending attack.</summary>
        /// <summary>Destroy an Ingeminex through a card EFFECT (Doom Gate) rather than by
        /// spending power on it. Destroying is defeating: `destroyerIndex` collects the
        /// printed reward exactly as the attack path does — pass -1 only for a kill that
        /// belongs to nobody.</summary>
        public void DestroyActiveMonster(ShardsCard monster, int destroyerIndex = -1)
        {
            if (monster == null || !State.ActiveMonsters.Remove(monster)) return;
            State.PendingMonsterAttacks.Remove(monster.InstanceId);
            monster.DamageThisTurn = 0;
            monster.Zone = ShardsZone.CenterDeck;
            State.CenterDeck.Insert(0, monster);
            Emit(new ShardsMonsterDefeatedEvent { PlayerIndex = destroyerIndex, InstanceId = monster.InstanceId, DefId = monster.DefId });

            if (destroyerIndex >= 0 && monster.Def.RewardEffect != null)
                QueueEffect(monster.Def.RewardEffect, destroyerIndex, monster);
        }

        /// <summary>Fast-play a center-owned card that is NOT in the row (Longshot reveals
        /// it off the center-deck top). Same fast-play rules as Warp: effect now, play
        /// zone, faction play counted, bottom of the center deck at cleanup.</summary>
        public void FastPlayLoose(int playerIndex, ShardsCard card)
        {
            if (card.Def.CannotBeFastPlayed) return; // Comet: must be BOUGHT
            var player = State.Players[playerIndex];
            card.Owner = playerIndex;
            card.Zone = ShardsZone.PlayZone;
            card.FastPlayed = true;
            player.PlayZone.Add(card);
            Emit(new ShardsCardBoughtEvent { PlayerIndex = playerIndex, SlotIndex = -1, DefId = card.DefId, CostPaid = 0, FastPlay = true });
            CountPlay(player, card);
            QueuePlayEffect(player, card);
        }

        /// <summary>The end-turn tail: runs after cleanup AND after any post-redraw
        /// Ingeminex attacks have fully resolved.</summary>
        private void AdvanceAfterEndTurn(ShardsPlayer player)
        {
            _endTurnInProgress = false;
            CheckStateBased();
            if (!State.GameOver)
            {
                // Slipstream Shard M20: the player takes another turn (once per game).
                if (State.ExtraTurnForPlayer == player.Index && !player.Eliminated)
                {
                    State.ExtraTurnForPlayer = -1;
                    StartTurn(player.Index, firstTurn: false);
                }
                else
                {
                    State.ExtraTurnForPlayer = -1;
                    AdvanceTurn();
                }
            }
            RoutePriority();
        }

        private void AdvanceTurn()
        {
            int next = State.TurnPlayerIndex;
            do
            {
                next = (next + 1) % State.Players.Count;
                if (next == 0) State.Round++;
            } while (State.Players[next].Eliminated);
            StartTurn(next, firstTurn: false);
        }

        private void StartTurn(int playerIndex, bool firstTurn)
        {
            // Readying happens in the END phase (champions/destinies/character), not here.
            State.TurnPlayerIndex = playerIndex;
            // Praetorian-02 (Duel): the "shields doubled until your next turn" window closes.
            State.Players[playerIndex].ShieldsDoubledUntilNextTurn = false;
            foreach (var champion in State.Players[playerIndex].Champions)
                champion.TemporaryDefenseUntilNextTurn = 0;
            ResolveLethalChampions(-1);
            Emit(new ShardsTurnStartedEvent { PlayerIndex = playerIndex, Round = State.Round });
        }

        // ------------------------------------------------------------------ effects pump

        private void QueueEffect(IShardsEffect effect, int controller, ShardsCard source)
        {
            _effectQueue.Enqueue((effect, new ShardsContext { Engine = this, ControllerIndex = controller, Source = source }));
        }

        private void Pump()
        {
            PumpEffects();
            if (PendingInput == null && !State.GameOver)
                RoutePriority();
        }

        private void PumpEffects()
        {
            // Re-entrancy guard: while a decision is pending, the active iterator is
            // parked at its yield — resuming it without an Answer would crash. Only the
            // decision-answer path (which clears PendingInput first) may resume it.
            if (PendingInput != null && PendingInput.Kind == PendingInputKind.Decision)
                return;

            while (true)
            {
                if (_activeEffect != null)
                {
                    if (_activeEffect.MoveNext())
                    {
                        var step = _activeEffect.Current;
                        if (step?.Decision != null)
                        {
                            step.Decision.Id = step.Decision.Id == 0 ? State.NextDecisionId++ : step.Decision.Id;
                            PendingInput = PendingInput.ForDecision(step.Decision);
                            Emit(new DecisionRequestedEvent
                            {
                                PlayerIndex = step.Decision.PlayerIndex,
                                DecisionId = step.Decision.Id,
                                Title = step.Decision.Title
                            });
                            return; // paused until the answer arrives
                        }
                        continue;
                    }
                    _activeEffect = null;
                    _activeContext = null;
                }

                CheckStateBased();
                if (State.GameOver) return;

                if (_effectQueue.Count == 0)
                {
                    // Refresh priority whenever no decision pends — legal actions change
                    // after every action, and the active player may have been eliminated
                    // by their own effect (RoutePriority passes the turn on then).
                    if (PendingInput == null || PendingInput.Kind != PendingInputKind.Decision)
                        RoutePriority();
                    return;
                }
                var (effect, ctx) = _effectQueue.Dequeue();
                _activeContext = ctx;
                _activeEffect = effect.Resolve(ctx).GetEnumerator();
            }
        }

        private void ResolveLethalChampions(int byPlayer)
        {
            if ((State.Dlc & ShardsDlc.Duel) == 0) return;
            while (true)
            {
                var doomed = new List<(ShardsPlayer owner, ShardsCard champion)>();
                foreach (var owner in State.Players)
                    foreach (var champion in owner.Champions)
                        if (champion.DamageThisTurn >= EffectiveDefense(owner, champion))
                            doomed.Add((owner, champion));
                if (doomed.Count == 0) return;
                // Determine the whole wave before changing an aura source's zone.
                foreach (var (owner, champion) in doomed)
                    DestroyChampion(owner, champion, byPlayer);
            }
        }

        private void CheckStateBased()
        {
            if (State.GameOver) return;
            ResolveLethalChampions(State.TurnPlayerIndex);
            int living = State.LivingCount;
            if (living == 0)
            {
                // Simultaneous drop below 1 (possible with "lose Health" effects) ⇒ TIE.
                State.GameOver = true;
                State.WinnerIndex = -1;
                Emit(new ShardsGameEndedEvent { WinnerIndex = -1 });
                PendingInput = null;
            }
            else if (living == 1)
            {
                foreach (var p in State.Players)
                    if (!p.Eliminated)
                    {
                        State.GameOver = true;
                        State.WinnerIndex = p.Index;
                        Emit(new ShardsGameEndedEvent { WinnerIndex = p.Index });
                        PendingInput = null;
                    }
            }
        }

        private void RoutePriority()
        {
            if (State.GameOver) { PendingInput = null; return; }
            if (PendingInput != null && PendingInput.Kind == PendingInputKind.Decision) return;
            // The active player can eliminate THEMSELVES mid-turn (Bound for Life,
            // Oblivion Gatekeeper) — pass the turn on instead of deadlocking.
            if (State.TurnPlayer.Eliminated && !_endTurnInProgress)
                AdvanceTurn();
            PendingInput = PendingInput.Priority(State.TurnPlayerIndex, LegalActions(State.TurnPlayerIndex));
        }

        // ------------------------------------------------------------------ legality

        public List<PlayerAction> LegalActions(int playerIndex)
        {
            var actions = new List<PlayerAction>();
            var player = State.Players[playerIndex];
            if (player.Eliminated || State.GameOver || playerIndex != State.TurnPlayerIndex)
                return actions;

            foreach (var card in player.Hand)
                actions.Add(new ShardsPlayCardAction { PlayerIndex = playerIndex, CardInstanceId = card.InstanceId });

            bool duel = (State.Dlc & ShardsDlc.Duel) != 0;
            for (int s = 0; s < State.CenterRow.Length; s++)
            {
                var card = State.CenterRow[s];
                if (card == null) continue;
                var def = card.Def;
                if (player.Gems >= EffectiveCost(player, def))
                {
                    actions.Add(new ShardsBuyCardAction { PlayerIndex = playerIndex, SlotIndex = s });
                    if (def.Type == ShardsCardType.Mercenary)
                        actions.Add(new ShardsBuyCardAction { PlayerIndex = playerIndex, SlotIndex = s, FastPlay = true });
                }
                // Duel: reroll this slot at the climbing per-turn price (card may opt out).
                if (duel && !def.CannotBeRerolled && player.Gems >= RerollCost(player))
                    actions.Add(new ShardsRerollRowAction { PlayerIndex = playerIndex, SlotIndex = s });
            }

            if (!player.FocusedThisTurn && !player.CharacterExhausted && player.Gems >= 1)
                actions.Add(new ShardsFocusAction { PlayerIndex = playerIndex });

            // Duel: the hero's unique ability (separate from Focus, once per turn).
            if (HeroAbilityAvailable(player))
                actions.Add(new ShardsHeroAbilityAction { PlayerIndex = playerIndex });

            foreach (var champion in player.Champions)
                if (!champion.Exhausted && champion.Def.ExhaustEffect != null &&
                    player.Gems >= champion.Def.ExhaustGemCost)
                    actions.Add(new ShardsExhaustAction { PlayerIndex = playerIndex, CardInstanceId = champion.InstanceId });
            foreach (var destiny in player.Destinies)
                if (!destiny.Exhausted && destiny.Def.ExhaustEffect != null &&
                    player.Gems >= destiny.Def.ExhaustGemCost)
                    actions.Add(new ShardsExhaustAction { PlayerIndex = playerIndex, CardInstanceId = destiny.InstanceId });

            if (duel)
                foreach (var opponent in State.LivingOpponentsOf(playerIndex))
                    foreach (var champion in opponent.Champions)
                    {
                        int remaining = System.Math.Max(0, EffectiveDefense(opponent, champion) - champion.DamageThisTurn);
                        if (player.Power >= remaining && CanAttackChampion(player, opponent, champion))
                            actions.Add(new ShardsAttackChampionAction
                            {
                                PlayerIndex = playerIndex, TargetPlayerIndex = opponent.Index,
                                CardInstanceId = champion.InstanceId, Amount = remaining
                            });
                    }
            foreach (var monster in State.ActiveMonsters)
                if (player.Power >= monster.Def.Defense - monster.DamageThisTurn)
                    actions.Add(new ShardsAttackMonsterAction { PlayerIndex = playerIndex, CardInstanceId = monster.InstanceId });

            if ((State.Dlc & ShardsDlc.IntoTheHorizon) != 0 && !player.DestinyTaken && player.Mastery >= 5)
                foreach (var destiny in State.DestinyRow)
                    actions.Add(new ShardsTakeDestinyAction { PlayerIndex = playerIndex, CardInstanceId = destiny.InstanceId });

            if (!player.RelicRecruited && player.Mastery >= 10)
                foreach (var relic in player.SetAside)
                    if (relic.Def.Type == ShardsCardType.Relic)
                        actions.Add(new ShardsRecruitRelicAction { PlayerIndex = playerIndex, CardInstanceId = relic.InstanceId });

            actions.Add(new ShardsEndTurnAction { PlayerIndex = playerIndex });
            actions.Add(new ConcedeAction { PlayerIndex = playerIndex });
            return actions;
        }

        // ------------------------------------------------------------------ mutation helpers (effects call these)

        public void Emit(GameEvent e)
        {
            Log.Append(e);
        }

        /// <summary>DNA: all armed effects copy the same next actual recruitment.
        /// ResetTurn expires unused copies. Activating the effect pays no additional cost.</summary>
        public void ArmRecruitCopy(int playerIndex)
        {
            State.Players[playerIndex].PendingRecruitCopies++;
        }

        /// <summary>Call once for an actual acquisition, including free and relic
        /// recruits, never a loan, return, or the copy itself. The original retains its
        /// normal routing; each fresh copy goes directly to discard.</summary>
        public void NotifyRecruit(ShardsPlayer player, ShardsCard recruited)
        {
            int copies = player.PendingRecruitCopies;
            if (copies <= 0) return;
            player.PendingRecruitCopies = 0;
            for (int i = 0; i < copies; i++)
            {
                var copy = NewCard(recruited.DefId, player.Index, ShardsZone.Discard);
                State.GeneratedCardCounts.TryGetValue(copy.DefId, out int generated);
                State.GeneratedCardCounts[copy.DefId] = generated + 1;
                player.Discard.Add(copy);
                Emit(new ShardsCardCopiedEvent
                {
                    PlayerIndex = player.Index, SourceInstanceId = recruited.InstanceId,
                    InstanceId = copy.InstanceId, DefId = copy.DefId
                });
            }
        }

        public void GainGems(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            player.Gems += amount;
            Emit(new ShardsGemsChangedEvent { PlayerIndex = playerIndex, Delta = amount, NewValue = player.Gems });
        }

        public void GainPower(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            player.Power += amount;
            Emit(new ShardsPowerChangedEvent { PlayerIndex = playerIndex, Delta = amount, NewValue = player.Power });
        }

        public void GainMastery(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            int before = player.Mastery;
            player.Mastery = System.Math.Min(State.Rules.MasteryCap, player.Mastery + amount);
            if (player.Mastery != before)
                Emit(new ShardsMasteryChangedEvent { PlayerIndex = playerIndex, Delta = player.Mastery - before, NewValue = player.Mastery });
        }

        /// <summary>Mastery can be LOST to card effects (e.g. Venator of the Wastes,
        /// Ingeminex Torment) — floor at 0; thresholds/relics already earned stay earned.</summary>
        public void LoseMastery(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            int before = player.Mastery;
            player.Mastery = System.Math.Max(0, player.Mastery - amount);
            if (player.Mastery != before)
                Emit(new ShardsMasteryChangedEvent { PlayerIndex = playerIndex, Delta = player.Mastery - before, NewValue = player.Mastery });
        }

        public void GainHealth(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            // Lifebloom Ritual (Duel): all healing this turn is doubled.
            if (player.HealingDoubledThisTurn && amount > 0)
                amount *= 2;
            int before = player.Health;
            player.Health = System.Math.Min(State.Rules.MaxHealth, player.Health + amount);
            if (player.Health != before)
                Emit(new ShardsHealthChangedEvent { PlayerIndex = playerIndex, Delta = player.Health - before, NewValue = player.Health });
            // Entropic Talons: health gained this turn also grants that much power — and
            // "would gain at the 50 cap" still counts (FAQ), so use the REQUESTED amount.
            if (player.HealthToPowerThisTurn && amount > 0)
                GainPower(playerIndex, amount);
            // Nectar Alchemist (Duel): only the OVER-cap remainder becomes power.
            if (player.OverflowHealthToPowerThisTurn && amount > 0)
            {
                int overflow = amount - (player.Health - before);
                if (overflow > 0) GainPower(playerIndex, overflow);
            }
        }

        /// <summary>"Lose Health" is NOT damage: shields can never prevent it and it does
        /// not count as unblocked damage. It CAN cause simultaneous-death ties.</summary>
        public void LoseHealth(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            if (player.Eliminated || amount <= 0) return;
            player.Health -= amount;
            Emit(new ShardsHealthChangedEvent { PlayerIndex = playerIndex, Delta = -amount, NewValue = player.Health });
            if (player.Health <= 0)
                EliminatePlayer(player);
        }

        /// <summary>Warp (effect keyword, "Warp N"): fast-play a center-row card for FREE.
        /// Card effects call this after their choose-a-row-card decision. The warped card
        /// follows fast-play rules — effect now, play zone, faction play counted, bottom
        /// of the center deck at cleanup.</summary>
        public bool WarpFromRow(int playerIndex, int slotIndex, bool keep = false)
        {
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length) return false;
            var card = State.CenterRow[slotIndex];
            if (card == null) return false;
            if (card.Def.CannotBeFastPlayed) return false; // Comet: must be BOUGHT
            var player = State.Players[playerIndex];

            State.CenterRow[slotIndex] = null;
            RefillSlot(slotIndex);

            Emit(new ShardsCardBoughtEvent
            {
                PlayerIndex = playerIndex,
                SlotIndex = slotIndex,
                DefId = card.DefId,
                CostPaid = 0,
                FastPlay = true
            });

            card.Owner = playerIndex;
            card.Zone = ShardsZone.PlayZone;
            // keep (Deadly Recruits): NOT the Warp keyword — the card is yours and goes
            // to your discard at cleanup instead of the bottom of the center deck.
            card.FastPlayed = !keep;
            player.PlayZone.Add(card);
            CountPlay(player, card);
            QueuePlayEffect(player, card);
            return true;
        }

        /// <summary>Queue a played card's effect from ANY play path (hand, fast-play,
        /// warp). General Decurion M20: Homodeus ally effects resolve a second time —
        /// fast-played mercenaries count as played allies, so every path doubles.</summary>
        private void QueuePlayEffect(ShardsPlayer player, ShardsCard card)
        {
            var def = card.Def;
            if (def.PlayEffect == null) return;
            QueueEffect(def.PlayEffect, player.Index, card);
            if (player.CopyHomodeusAlliesThisTurn && def.Faction == ShardsFaction.Homodeus && !def.IsChampion)
                QueueEffect(def.PlayEffect, player.Index, card);
        }

        /// <summary>Reveal/take the top card of the CENTER deck (The Shard Defiant).
        /// Ingeminex bypass to their space and the next card is taken instead.</summary>
        public ShardsCard DrawFromCenterDeck()
        {
            while (State.CenterDeck.Count > 0)
            {
                var card = State.CenterDeck[State.CenterDeck.Count - 1];
                State.CenterDeck.RemoveAt(State.CenterDeck.Count - 1);
                if (card.Def.IsMonster)
                {
                    card.Zone = ShardsZone.MonsterSpace;
                    card.DamageThisTurn = 0;
                    State.ActiveMonsters.Add(card);
                    State.PendingMonsterAttacks.Add(card.InstanceId);
                    Emit(new ShardsMonsterRevealedEvent { InstanceId = card.InstanceId, DefId = card.DefId });
                    continue;
                }
                return card;
            }
            return null;
        }

        /// <summary>Recruit a card taken off the center deck (Shard Defiant "recruit it").</summary>
        public void RecruitLoose(ShardsPlayer player, ShardsCard card)
        {
            if (card.Def.CannotBeFastPlayed) return; // Comet requires a normal purchase.
            card.Owner = player.Index;
            card.FastPlayed = false;
            Emit(new ShardsCardBoughtEvent { PlayerIndex = player.Index, SlotIndex = -1, DefId = card.DefId, CostPaid = 0, FastPlay = false });
            RecruitTo(player, card);
        }

        /// <summary>Top of a personal deck, reshuffling the discard in if needed (reveal
        /// effects can never deck out). Null if deck AND discard are empty.</summary>
        public ShardsCard PeekTopOfDeck(ShardsPlayer player)
        {
            if (player.Deck.Count == 0)
            {
                if (player.Discard.Count == 0) return null;
                foreach (var card in player.Discard)
                {
                    card.Zone = ShardsZone.Deck;
                    player.Deck.Add(card);
                }
                player.Discard.Clear();
                State.Rng.Shuffle(player.Deck);
                Emit(new ShardsDeckShuffledEvent { PlayerIndex = player.Index });
            }
            return player.Deck[player.Deck.Count - 1];
        }

        /// <summary>Split-decision option ids at or above this value target a champion
        /// (id = base + champion instance id); below it they are player indexes.</summary>
        public const int ChampionSplitBase = 100000;

        /// <summary>Apply assigned power to a champion: marks accumulate within the
        /// turn; the champion dies once its full effective defense is reached. Shared by
        /// the AttackChampion action and the end-turn damage split.</summary>
        private void ApplyPowerToChampion(int attackerIndex, ShardsPlayer owner, ShardsCard champion, int amount)
        {
            if (amount <= 0) return;
            champion.DamageThisTurn += amount;
            if (champion.DamageThisTurn >= EffectiveDefense(owner, champion))
            {
                DestroyChampion(owner, champion, attackerIndex);
            }
            else
            {
                Emit(new ShardsChampionDamagedEvent
                {
                    OwnerIndex = owner.Index,
                    ByPlayerIndex = attackerIndex,
                    InstanceId = champion.InstanceId,
                    DefId = champion.DefId,
                    Amount = amount,
                    Total = champion.DamageThisTurn
                });
            }
        }

        /// <summary>Destroy a champion by card effect (bypasses defense entirely — a
        /// destroy-effect can kill even an unattackable champion) or by lethal marks.
        /// byPlayer -1 = a game effect (Ingeminex Malice).</summary>
        public void DestroyChampion(ShardsPlayer owner, ShardsCard champion, int byPlayer)
        {
            if (!owner.Champions.Remove(champion)) return;
            champion.Zone = ShardsZone.Discard;
            champion.TemporaryDefenseUntilNextTurn = 0;
            champion.DamageThisTurn = 0;
            owner.Discard.Add(champion);
            Emit(new ShardsChampionDestroyedEvent
            {
                OwnerIndex = owner.Index,
                ByPlayerIndex = byPlayer,
                InstanceId = champion.InstanceId,
                DefId = champion.DefId
            });
        }

        /// <summary>Recruit a row card for free by effect (Portal Monk, The Crystal Gate).
        /// toHand: the card goes to hand instead of discard. Redirect flags still apply
        /// on the normal path via RecruitTo.</summary>
        public bool RecruitFromRowFree(int playerIndex, int slotIndex, bool toHand)
        {
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length) return false;
            var card = State.CenterRow[slotIndex];
            if (card == null) return false;
            if (card.Def.CannotBeFastPlayed) return false; // Comet requires a normal purchase.
            var player = State.Players[playerIndex];

            State.CenterRow[slotIndex] = null;
            RefillSlot(slotIndex);
            Emit(new ShardsCardBoughtEvent
            {
                PlayerIndex = playerIndex,
                SlotIndex = slotIndex,
                DefId = card.DefId,
                CostPaid = 0,
                FastPlay = false
            });

            card.Owner = playerIndex;
            card.FastPlayed = false;
            if (toHand)
            {
                NotifyRecruit(player, card);
                card.Zone = ShardsZone.Hand;
                player.Hand.Add(card);
                Emit(new ShardsCardReturnedEvent { PlayerIndex = playerIndex, InstanceId = card.InstanceId, DefId = card.DefId });
            }
            else
            {
                RecruitTo(player, card);
            }
            return true;
        }

        /// <summary>Banish: move a card to the shared removed-from-game pile.</summary>
        public void Banish(ShardsCard card, List<ShardsCard> fromZone)
        {
            if (!fromZone.Remove(card)) return;
            bool fromHand = card.Zone == ShardsZone.Hand;
            card.Zone = ShardsZone.Banished;
            card.TemporaryDefenseUntilNextTurn = 0;
            State.Banished.Add(card);
            // "Cards you banished this turn" (Warpquartz Duel) — banishes are always an
            // active-player effect, so they attribute to the turn player.
            if (!State.GameOver)
                State.TurnPlayer.CardsBanishedThisTurn++;
            Emit(new ShardsCardBanishedEvent { PlayerIndex = card.Owner, InstanceId = card.InstanceId, DefId = card.DefId, FromHand = fromHand });
        }

        public void DrawCards(int playerIndex, int count)
        {
            var player = State.Players[playerIndex];
            for (int i = 0; i < count; i++)
                DrawOne(player);
        }

        private void DrawOne(ShardsPlayer player)
        {
            if (player.Deck.Count == 0)
            {
                if (player.Discard.Count == 0) return; // nothing to draw anywhere
                foreach (var card in player.Discard)
                {
                    card.Zone = ShardsZone.Deck;
                    player.Deck.Add(card);
                }
                player.Discard.Clear();
                State.Rng.Shuffle(player.Deck);
                Emit(new ShardsDeckShuffledEvent { PlayerIndex = player.Index });
            }
            var drawn = player.Deck[player.Deck.Count - 1];
            player.Deck.RemoveAt(player.Deck.Count - 1);
            drawn.Zone = ShardsZone.Hand;
            player.Hand.Add(drawn);
            Emit(new ShardsCardDrawnEvent { PlayerIndex = player.Index, InstanceId = drawn.InstanceId, DefId = drawn.DefId });
        }

        private void RefillSlot(int slotIndex)
        {
            while (State.CenterDeck.Count > 0)
            {
                var card = State.CenterDeck[State.CenterDeck.Count - 1];
                State.CenterDeck.RemoveAt(State.CenterDeck.Count - 1);

                if (_suppressedOpeningCards != null && (State.Dlc & ShardsDlc.Duel) != 0 &&
                    card.Def.Cost >= 6 && card.DefId != "comet")
                {
                    _suppressedOpeningCards.Add(card);
                    continue;
                }
                if (card.Def.IsMonster)
                {
                    if (_suppressedOpeningCards != null)
                    {
                        // Initial setup: don't reveal — hold to reshuffle afterward so
                        // no Ingeminex attacks on turn 1 (see the fill site in Setup).
                        _suppressedOpeningCards.Add(card);
                        continue;
                    }
                    // Ingeminex never enter the row: they go face up to their own space
                    // and the NEXT center-deck card replaces them for the refill. Their
                    // attack fires once, at the end of the turn they were revealed on.
                    card.Zone = ShardsZone.MonsterSpace;
                    card.DamageThisTurn = 0;
                    State.ActiveMonsters.Add(card);
                    State.PendingMonsterAttacks.Add(card.InstanceId);
                    Emit(new ShardsMonsterRevealedEvent { InstanceId = card.InstanceId, DefId = card.DefId });
                    continue;
                }

                card.Zone = ShardsZone.CenterRow;
                State.CenterRow[slotIndex] = card;
                Emit(new ShardsRowRefilledEvent { SlotIndex = slotIndex, InstanceId = card.InstanceId, DefId = card.DefId });
                return;
            }
            State.CenterRow[slotIndex] = null; // center deck ran dry
        }

        private void EliminatePlayer(ShardsPlayer player)
        {
            if (player.Eliminated) return;
            player.Eliminated = true;

            // Their cards leave play with them — except fast-played/warped cards, which
            // belong to the CENTER deck and return to its bottom.
            foreach (var card in new List<ShardsCard>(player.PlayZone))
            {
                if (card.FastPlayed)
                {
                    card.FastPlayed = false;
                    card.Owner = -1;
                    card.Zone = ShardsZone.CenterDeck;
                    State.CenterDeck.Insert(0, card);
                }
                else
                {
                    card.Zone = ShardsZone.Discard;
                    player.Discard.Add(card);
                }
            }
            player.PlayZone.Clear();
            foreach (var champion in player.Champions)
            {
                champion.Zone = ShardsZone.Discard;
                champion.TemporaryDefenseUntilNextTurn = 0;
                champion.DamageThisTurn = 0;
                player.Discard.Add(champion);
            }
            player.Champions.Clear();

            Emit(new ShardsPlayerEliminatedEvent { PlayerIndex = player.Index });
            // NO CheckStateBased here: simultaneous eliminations (all players losing
            // health at once) must all land before the winner/tie check — the pump,
            // AfterDefenses and Concede run the check afterwards.
        }
    }
}
