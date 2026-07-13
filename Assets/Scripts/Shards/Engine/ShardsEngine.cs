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
        public readonly ShardsState State = new();
        public readonly EventLog Log = new();

        public PendingInput PendingInput { get; private set; }

        // Active effect resolution (paused on a decision) + the follow-up queue.
        private IEnumerator<ShardsStep> _activeEffect;
        private ShardsContext _activeContext;
        private readonly Queue<(IShardsEffect effect, ShardsContext ctx)> _effectQueue = new();

        // End-turn flow state (damage split → per-defender shield reveals → cleanup).
        private bool _endTurnInProgress;
        private Queue<(int defender, int amount)> _pendingDefenses;
        private List<int> _splitTargets;
        private List<int> _splitAmounts;
        /// <summary>Non-null only during the initial center-row fill: monsters drawn then
        /// are held here (not revealed) and shuffled back into the deck afterward.</summary>
        private List<ShardsCard> _suppressedMonsters;

        public ShardsEngine(ShardsConfig config)
        {
            Setup(config);
        }

        // ------------------------------------------------------------------ setup

        private void Setup(ShardsConfig config)
        {
            State.Rules = config.Rules;
            State.Dlc = config.Dlc;
            State.Rng = new DeterministicRng(config.Seed);

            // Center deck from the enabled sets (relics and destinies are never in it).
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Type == ShardsCardType.Starter ||
                    def.Type == ShardsCardType.Relic ||
                    def.Type == ShardsCardType.Destiny)
                    continue;
                bool inSet = def.Set switch
                {
                    "base" => true,
                    "relics_of_the_future" => (config.Dlc & ShardsDlc.RelicsOfTheFuture) != 0,
                    "shadow_of_salvation" => (config.Dlc & ShardsDlc.ShadowOfSalvation) != 0,
                    "into_the_horizon" => (config.Dlc & ShardsDlc.IntoTheHorizon) != 0,
                    _ => false
                };
                if (!inSet) continue;
                // ItH rule: Corruption's reward needs relics — remove it without RotF.
                if (def.IsMonster && def.Id == "ingeminex_corruption" &&
                    (config.Dlc & ShardsDlc.RelicsOfTheFuture) == 0)
                    continue;
                // SoS ships errata replacements for RotF's Cloud Oracles: with both sets
                // enabled only the replacement copies play (PvP-identical wording fix).
                if (def.Id == "cloud_oracles" && (config.Dlc & ShardsDlc.ShadowOfSalvation) != 0)
                    continue;
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
                    if (def.Type == ShardsCardType.Destiny)
                        for (int i = 0; i < def.Quantity; i++)
                            destinies.Add(NewCard(def.Id, -1, ShardsZone.DestinyRow));
                State.Rng.Shuffle(destinies);
                for (int i = 0; i < destinies.Count; i++)
                {
                    if (i < 6) State.DestinyRow.Add(destinies[i]);
                    else State.DestinyDeck.Add(destinies[i]);
                }
            }

            // Players: starter decks, staggered mastery 0/1/2/3, opening hands.
            for (int i = 0; i < config.Players.Count; i++)
            {
                var spec = config.Players[i];
                var player = new ShardsPlayer
                {
                    Index = i,
                    Name = spec.Name,
                    CharacterId = spec.CharacterId,
                    FullControl = spec.FullControl,
                    Health = config.Rules.StartingHealth,
                    Mastery = i // staggered start: 0/1/2/3 by turn order
                };
                foreach (var def in ShardsCardDatabase.All)
                {
                    if (def.Type != ShardsCardType.Starter) continue;
                    for (int c = 0; c < def.Quantity; c++)
                        player.Deck.Add(NewCard(def.Id, i, ShardsZone.Deck));
                }
                State.Rng.Shuffle(player.Deck);

                // Relics are set aside when the SET that ships them is enabled (recruit
                // ONE free at Mastery 10): RotF ships the four base characters' pairs,
                // SoS ships Rez's — the SoS sheet grants Rez his relics with SoS alone.
                foreach (var def in ShardsCardDatabase.All)
                {
                    if (def.Type != ShardsCardType.Relic || def.Character != spec.CharacterId) continue;
                    bool shipped = def.Set switch
                    {
                        "relics_of_the_future" => (config.Dlc & ShardsDlc.RelicsOfTheFuture) != 0,
                        "shadow_of_salvation" => (config.Dlc & ShardsDlc.ShadowOfSalvation) != 0,
                        _ => false
                    };
                    if (shipped)
                        player.SetAside.Add(NewCard(def.Id, i, ShardsZone.SetAside));
                }

                State.Players.Add(player);
            }

            // Center row. Monsters revealed during this INITIAL fill would attack on
            // turn 1 before anyone has acted, so (design decision) hold any drawn
            // Ingeminex aside and reshuffle them back into the center deck once the row
            // is full — the opening board is always monster-free.
            State.CenterRow = new ShardsCard[config.Rules.CenterRowSize];
            _suppressedMonsters = new List<ShardsCard>();
            for (int s = 0; s < State.CenterRow.Length; s++)
                RefillSlot(s);
            if (_suppressedMonsters.Count > 0)
            {
                foreach (var monster in _suppressedMonsters)
                {
                    monster.Zone = ShardsZone.CenterDeck;
                    State.CenterDeck.Add(monster);
                }
                State.Rng.Shuffle(State.CenterDeck);
            }
            _suppressedMonsters = null;

            Emit(new ShardsGameStartedEvent { PlayerCount = config.Players.Count, Dlc = (int)config.Dlc });

            foreach (var player in State.Players)
                for (int d = 0; d < config.Rules.HandSize; d++)
                    DrawOne(player);

            StartTurn(0, firstTurn: true);
            RoutePriority();
        }

        private ShardsCard NewCard(string defId, int owner, ShardsZone zone) => new()
        {
            InstanceId = State.NextInstanceId++,
            DefId = defId,
            Owner = owner,
            Zone = zone
        };

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
                    return action is ConcedeAction concede ? Concede(concede.PlayerIndex)
                        : SubmitResult.Rejected("A decision is pending");
                if (decision.Answer == null || decision.Answer.DecisionId != PendingInput.Decision.Id)
                    return SubmitResult.Rejected("Answer does not match the pending decision");
                var error = ValidateAnswer(PendingInput.Decision, decision.Answer);
                if (error != null)
                    return SubmitResult.Rejected(error);

                var request = PendingInput.Decision;
                PendingInput = null;
                Emit(new DecisionMadeEvent { PlayerIndex = action.PlayerIndex, DecisionId = request.Id });
                _activeContext.Answer = decision.Answer;
                PumpEffects();
                return SubmitResult.Ok();
            }

            var result = ExecuteTurnAction(action);
            if (result.Accepted)
                Pump();
            return result;
        }

        private static string ValidateAnswer(DecisionRequest request, DecisionAnswer answer)
        {
            if (answer.ChosenOptionIds.Count < request.Min) return "Too few options chosen";
            if (answer.ChosenOptionIds.Count > request.Max) return "Too many options chosen";
            foreach (int id in answer.ChosenOptionIds)
            {
                bool known = false;
                foreach (var option in request.Options)
                    if (option.Id == id)
                        known = true;
                if (!known) return "Unknown option " + id;
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
                case ShardsAttackChampionAction attack:
                    return AttackChampion(player, attack.TargetPlayerIndex, attack.CardInstanceId, attack.Amount);
                case ShardsAttackMonsterAction monster:
                    return AttackMonster(player, monster.CardInstanceId, monster.Amount);
                case ShardsTakeDestinyAction destiny:
                    return TakeDestiny(player, destiny.CardInstanceId);
                case ShardsRecruitRelicAction relic:
                    return RecruitRelic(player, relic.CardInstanceId);
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
            int cost = EffectiveCost(player, def);
            if (player.Gems < cost) return SubmitResult.Rejected("Not enough gems");

            if (cost > 0)
            {
                player.Gems -= cost;
                Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -cost, NewValue = player.Gems });
            }

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
            var def = card.Def;
            if (player.NextHomodeusChampionsIntoPlay > 0 && def.IsChampion && def.Faction == ShardsFaction.Homodeus)
            {
                player.NextHomodeusChampionsIntoPlay--;
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
            Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
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

        private SubmitResult ExhaustCard(ShardsPlayer player, int instanceId)
        {
            // Champions and owned Destinies both carry once-per-turn exhaust powers.
            var card = player.Champions.Find(c => c.InstanceId == instanceId)
                       ?? player.Destinies.Find(c => c.InstanceId == instanceId);
            if (card == null) return SubmitResult.Rejected("Card not in play");
            if (card.Exhausted) return SubmitResult.Rejected("Already exhausted this turn");
            var def = card.Def;
            if (def.ExhaustEffect == null) return SubmitResult.Rejected("No exhaust ability");

            card.Exhausted = true;
            Emit(new ShardsCharacterExhaustedEvent { PlayerIndex = player.Index, CardInstanceId = card.InstanceId });
            QueueEffect(def.ExhaustEffect, player.Index, card);
            return SubmitResult.Ok();
        }

        private SubmitResult AttackChampion(ShardsPlayer player, int targetPlayer, int instanceId, int amount)
        {
            if (targetPlayer < 0 || targetPlayer >= State.Players.Count || targetPlayer == player.Index)
                return SubmitResult.Rejected("Invalid target player");
            var owner = State.Players[targetPlayer];
            var champion = owner.Champions.Find(c => c.InstanceId == instanceId);
            if (champion == null) return SubmitResult.Rejected("Champion not in play");
            if (!CanAttackChampion(player, owner, champion))
                return SubmitResult.Rejected("That champion can't be attacked");
            var def = champion.Def;

            // Within-turn accumulation: damage marks persist across attacks this turn and
            // evaporate at end of turn — a champion dies only if ONE player deals its full
            // defense within a single turn. Amount 0 = spend exactly what's still needed.
            int remaining = EffectiveDefense(owner, champion) - champion.DamageThisTurn;
            int spend = amount <= 0 ? remaining : System.Math.Min(amount, remaining);
            if (spend <= 0) return SubmitResult.Rejected("Champion already fully damaged");
            if (player.Power < spend) return SubmitResult.Rejected("Not enough power");

            player.Power -= spend;
            Emit(new ShardsPowerChangedEvent { PlayerIndex = player.Index, Delta = -spend, NewValue = player.Power });
            ApplyPowerToChampion(player.Index, owner, champion, spend);
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
            return SubmitResult.Ok();
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
            return value;
        }

        /// <summary>Faction identity check honoring Project Yggdrasil (the owner's Wraethe
        /// cards also count as Undergrowth and vice versa).</summary>
        public static bool CountsAs(ShardsPlayer owner, ShardsCardDef def, ShardsFaction faction)
        {
            if (def.Faction == faction) return true;
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
            player.CountFactionPlay(def.Faction, isAlly);
            if (player.Destinies.Exists(d => d.DefId == "project_yggdrasil"))
            {
                if (def.Faction == ShardsFaction.Wraethe)
                    player.CountFactionPlay(ShardsFaction.Undergrowth, isAlly);
                else if (def.Faction == ShardsFaction.Undergrowth)
                    player.CountFactionPlay(ShardsFaction.Wraethe, isAlly);
            }
            player.PlayedThisTurn.Add(card);

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
            return System.Math.Max(0, cost);
        }

        /// <summary>Printed defense plus auras from the owner's champions and destinies
        /// (Ferrata Guard, One Mind One Army).</summary>
        public int EffectiveDefense(ShardsPlayer owner, ShardsCard champion)
        {
            int defense = champion.Def.Defense;
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
                if (other != champion && other.Def.Taunt)
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

        private void BeginEndTurn(ShardsPlayer player)
        {
            _endTurnInProgress = true;

            var opponents = new List<ShardsPlayer>(State.LivingOpponentsOf(player.Index));
            opponents.RemoveAll(o => !CanAssignDamageTo(o)); // Taunt champions protect their owner

            // Enemy champions are assignable too (same power pool; marks accumulate
            // and reset at end of turn exactly like mid-turn attacks).
            var championTargets = new List<(ShardsPlayer owner, ShardsCard champion)>();
            foreach (var championOwner in State.LivingOpponentsOf(player.Index))
                foreach (var champion in championOwner.Champions)
                    if (CanAttackChampion(player, championOwner, champion))
                        championTargets.Add((championOwner, champion));

            if (player.Power > 0 && (opponents.Count > 0 || championTargets.Count > 0))
            {
                if (opponents.Count == 1 && championTargets.Count == 0)
                {
                    // Only one possible target — skip the split decision.
                    _splitTargets = new List<int> { opponents[0].Index };
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
                    // ALL remaining power); champion-only assignment (every player
                    // taunt-protected) is optional.
                    Min = opponents.Count > 0 ? player.Power : 0,
                    Max = player.Power,
                    Ordered = true
                };
                foreach (var opponent in opponents)
                    request.Options.Add(new DecisionOption(opponent.Index, opponent.Name));
                foreach (var (championOwner, champion) in championTargets)
                    request.Options.Add(new DecisionOption(ChampionSplitBase + champion.InstanceId,
                        champion.Def.Name + " (" + championOwner.Name + ")")
                    { CardInstanceId = champion.InstanceId, DefId = champion.DefId });
                // Defaults pad with DISTINCT options only — pre-fill a full assignment
                // (everything on the first opponent) so timeouts/bot-takeovers stay legal.
                if (opponents.Count > 0)
                    for (int i = 0; i < player.Power; i++)
                        request.DefaultOptionIds.Add(opponents[0].Index);
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

            // Champion damage lands first (shields never protect champions).
            foreach (var hit in championHits)
            {
                foreach (var championOwner in State.Players)
                {
                    var champion = championOwner.Champions.Find(c => c.InstanceId == hit.Key);
                    if (champion == null) continue;
                    ApplyPowerToChampion(ctx.ControllerIndex, championOwner, champion, hit.Value);
                    break;
                }
            }

            BeginDefenses(ctx.Controller);
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
                if (i >= 0 && _splitAmounts[i] > 0)
                    _pendingDefenses.Enqueue((seat, _splitAmounts[i]));
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
                    ApplyDamage(attacker.Index, defender, amount, 0, revealed: null);
                    continue;
                }

                // A champion's printed shield is INERT while in play (base game) — shields
                // are revealed FROM HAND only. Exception: cards flagged ShieldInPlay
                // (Praetorian-02) shield passively in play and NOT from hand.
                int passive = 0;
                foreach (var champion in defender.Champions)
                    if (champion.Def.ShieldInPlay)
                        passive += ShieldValue(defender, champion);

                var handShields = defender.Hand.FindAll(c => ShieldValue(defender, c) > 0 && !c.Def.ShieldInPlay);
                if (handShields.Count == 0)
                {
                    ApplyDamage(attacker.Index, defender, amount, passive, revealed: null);
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
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var card = defender.Hand.Find(c => c.InstanceId == id);
                if (card == null) continue;
                prevented += ShieldValue(defender, card);
                revealed.Add(card.DefId); // shields STAY in hand — reveal only
            }
            if (revealed.Count > 0)
                Emit(new ShardsShieldsRevealedEvent { PlayerIndex = defender.Index, DefIds = revealed, Prevented = prevented });

            ApplyDamage(attackerIndex, defender, amount, passive + prevented, revealed);
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
            CheckStateBased();
            if (State.GameOver) return;

            bool queued = false;

            // Swyft (while in play, character matches): fast-played cards may be KEPT
            // (recruited to discard) instead of returning to the center deck.
            bool canKeep = player.Champions.Exists(c =>
                c.Def.KeepFastPlaysCharacter != null && c.Def.KeepFastPlaysCharacter == player.CharacterId);
            if (canKeep && player.PlayZone.Exists(c => c.FastPlayed))
            {
                QueueEffect(new Custom(ctx => KeepFastPlaysFlow(ctx)), player.Index, null);
                queued = true;
            }

            if (State.PendingMonsterAttacks.Count > 0)
            {
                foreach (int id in new List<int>(State.PendingMonsterAttacks))
                {
                    var monster = State.ActiveMonsters.Find(m => m.InstanceId == id);
                    if (monster == null) continue;
                    Emit(new ShardsMonsterAttackedEvent { InstanceId = monster.InstanceId, DefId = monster.DefId });
                    if (monster.Def.MonsterAttackEffect != null)
                        QueueEffect(monster.Def.MonsterAttackEffect, player.Index, monster);
                }
                State.PendingMonsterAttacks.Clear();
                queued = true;
            }

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
            _endTurnInProgress = false;
            _splitTargets = null;
            _splitAmounts = null;

            // End phase, in rules order:
            // 1. fast-played/warped cards → BOTTOM of the center deck
            // 2. remaining play-zone cards → discard
            foreach (var card in player.PlayZone)
            {
                if (card.FastPlayed)
                {
                    card.FastPlayed = false;
                    card.Owner = -1;
                    card.Zone = ShardsZone.CenterDeck;
                    State.CenterDeck.Insert(0, card); // list end = top; index 0 = bottom
                    Emit(new ShardsMercenaryReturnedEvent { PlayerIndex = player.Index, DefId = card.DefId });
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
            Emit(new ShardsCleanupEvent { PlayerIndex = player.Index });

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

        private void CheckStateBased()
        {
            if (State.GameOver) return;
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
            }

            if (!player.FocusedThisTurn && !player.CharacterExhausted && player.Gems >= 1)
                actions.Add(new ShardsFocusAction { PlayerIndex = playerIndex });

            foreach (var champion in player.Champions)
                if (!champion.Exhausted && champion.Def.ExhaustEffect != null)
                    actions.Add(new ShardsExhaustAction { PlayerIndex = playerIndex, CardInstanceId = champion.InstanceId });
            foreach (var destiny in player.Destinies)
                if (!destiny.Exhausted && destiny.Def.ExhaustEffect != null)
                    actions.Add(new ShardsExhaustAction { PlayerIndex = playerIndex, CardInstanceId = destiny.InstanceId });

            // Only completing attacks are advertised (partial marking stays legal via an
            // explicit Amount — bots and defaults shouldn't waste power on partial hits).
            foreach (var opponent in State.LivingOpponentsOf(playerIndex))
                foreach (var champion in opponent.Champions)
                    if (CanAttackChampion(player, opponent, champion) &&
                        player.Power >= EffectiveDefense(opponent, champion) - champion.DamageThisTurn)
                        actions.Add(new ShardsAttackChampionAction
                        {
                            PlayerIndex = playerIndex,
                            TargetPlayerIndex = opponent.Index,
                            CardInstanceId = champion.InstanceId
                        });

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

        public void Emit(GameEvent e) => Log.Append(e);

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
            int before = player.Health;
            player.Health = System.Math.Min(State.Rules.MaxHealth, player.Health + amount);
            if (player.Health != before)
                Emit(new ShardsHealthChangedEvent { PlayerIndex = playerIndex, Delta = player.Health - before, NewValue = player.Health });
            // Entropic Talons: health gained this turn also grants that much power — and
            // "would gain at the 50 cap" still counts (FAQ), so use the REQUESTED amount.
            if (player.HealthToPowerThisTurn && amount > 0)
                GainPower(playerIndex, amount);
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
            card.Zone = ShardsZone.Banished;
            State.Banished.Add(card);
            Emit(new ShardsCardBanishedEvent { PlayerIndex = card.Owner, InstanceId = card.InstanceId, DefId = card.DefId });
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

                if (card.Def.IsMonster)
                {
                    if (_suppressedMonsters != null)
                    {
                        // Initial setup: don't reveal — hold to reshuffle afterward so
                        // no Ingeminex attacks on turn 1 (see the fill site in Setup).
                        _suppressedMonsters.Add(card);
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
