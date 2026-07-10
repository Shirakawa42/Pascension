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

            // Center deck from the enabled sets.
            foreach (var def in ShardsCardDatabase.All)
            {
                bool inSet = def.Set switch
                {
                    "base" => def.Type != ShardsCardType.Starter &&
                              def.Type != ShardsCardType.Relic && def.Type != ShardsCardType.Destiny,
                    "relics_of_the_future" => (config.Dlc & ShardsDlc.RelicsOfTheFuture) != 0 &&
                                              def.Type != ShardsCardType.Relic,
                    "shadow_of_salvation" => (config.Dlc & ShardsDlc.ShadowOfSalvation) != 0 &&
                                             def.Type != ShardsCardType.Relic,
                    "into_the_horizon" => (config.Dlc & ShardsDlc.IntoTheHorizon) != 0 &&
                                          def.Type != ShardsCardType.Destiny,
                    _ => false
                };
                if (!inSet || def.Type == ShardsCardType.Starter) continue;
                for (int i = 0; i < def.Quantity; i++)
                    State.CenterDeck.Add(NewCard(def.Id, -1, ShardsZone.CenterDeck));
            }
            State.Rng.Shuffle(State.CenterDeck);

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

                // DLC1: set the character's two relics aside. DLC3: destinies set aside.
                if ((config.Dlc & ShardsDlc.RelicsOfTheFuture) != 0)
                    foreach (var def in ShardsCardDatabase.All)
                        if (def.Type == ShardsCardType.Relic && def.Character == spec.CharacterId)
                            player.SetAside.Add(NewCard(def.Id, i, ShardsZone.SetAside));
                if ((config.Dlc & ShardsDlc.IntoTheHorizon) != 0)
                    foreach (var def in ShardsCardDatabase.All)
                        if (def.Type == ShardsCardType.Destiny && def.Character == spec.CharacterId)
                            player.SetAside.Add(NewCard(def.Id, i, ShardsZone.SetAside));

                State.Players.Add(player);
            }

            // Center row.
            State.CenterRow = new ShardsCard[config.Rules.CenterRowSize];
            for (int s = 0; s < State.CenterRow.Length; s++)
                RefillSlot(s);

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
                if (action is ShardsConcedeWrapper) { /* fallthrough for concede */ }
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

        // Internal marker (never serialized) so the decision branch can pattern-match cleanly.
        private sealed class ShardsConcedeWrapper : PlayerAction
        {
            public override string Describe() => "";
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
                    return AttackChampion(player, attack.TargetPlayerIndex, attack.CardInstanceId);
                case ShardsAttackMonsterAction monster:
                    return AttackMonster(player, monster.SlotIndex);
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
                player.Champions.Add(card);
                Emit(new ShardsChampionDeployedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
            }
            else
            {
                card.Zone = ShardsZone.PlayZone;
                player.PlayZone.Add(card);
            }
            player.CountFactionPlay(def.Faction);
            Emit(new ShardsCardPlayedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });

            if (def.PlayEffect != null)
                QueueEffect(def.PlayEffect, player.Index, card);
            return SubmitResult.Ok();
        }

        private SubmitResult BuyCard(ShardsPlayer player, int slotIndex, bool fastPlay)
        {
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length)
                return SubmitResult.Rejected("Invalid slot");
            var card = State.CenterRow[slotIndex];
            if (card == null) return SubmitResult.Rejected("Empty slot");
            var def = card.Def;
            if (def.IsMonster) return SubmitResult.Rejected("Monsters are fought, not bought");
            if (fastPlay && def.Type != ShardsCardType.Mercenary)
                return SubmitResult.Rejected("Only mercenaries can be fast-played");
            if (player.Gems < def.Cost) return SubmitResult.Rejected("Not enough gems");

            player.Gems -= def.Cost;
            Emit(new ShardsGemsChangedEvent { PlayerIndex = player.Index, Delta = -def.Cost, NewValue = player.Gems });

            // Row refills IMMEDIATELY — before the card's effect resolves.
            State.CenterRow[slotIndex] = null;
            RefillSlot(slotIndex);

            Emit(new ShardsCardBoughtEvent
            {
                PlayerIndex = player.Index,
                SlotIndex = slotIndex,
                DefId = card.DefId,
                CostPaid = def.Cost,
                FastPlay = fastPlay
            });

            if (fastPlay)
            {
                card.Owner = player.Index;
                card.Zone = ShardsZone.PlayZone;
                card.FastPlayed = true;
                player.PlayZone.Add(card);
                player.CountFactionPlay(def.Faction); // counts as playing an ally of its faction
                if (def.PlayEffect != null)
                    QueueEffect(def.PlayEffect, player.Index, card);
            }
            else
            {
                card.Owner = player.Index;
                card.Zone = ShardsZone.Discard;
                card.FastPlayed = false;
                player.Discard.Add(card);
            }
            return SubmitResult.Ok();
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
            var card = player.Champions.Find(c => c.InstanceId == instanceId);
            if (card == null) return SubmitResult.Rejected("Champion not in play");
            if (card.Exhausted) return SubmitResult.Rejected("Already exhausted this turn");
            var def = card.Def;
            if (def.ExhaustEffect == null) return SubmitResult.Rejected("No exhaust ability");

            card.Exhausted = true;
            Emit(new ShardsCharacterExhaustedEvent { PlayerIndex = player.Index, CardInstanceId = card.InstanceId });
            QueueEffect(def.ExhaustEffect, player.Index, card);
            return SubmitResult.Ok();
        }

        private SubmitResult AttackChampion(ShardsPlayer player, int targetPlayer, int instanceId)
        {
            if (targetPlayer < 0 || targetPlayer >= State.Players.Count || targetPlayer == player.Index)
                return SubmitResult.Rejected("Invalid target player");
            var owner = State.Players[targetPlayer];
            var champion = owner.Champions.Find(c => c.InstanceId == instanceId);
            if (champion == null) return SubmitResult.Rejected("Champion not in play");
            var def = champion.Def;
            // Champions must be destroyed whole — damage on them never persists.
            // (All-at-once model; TODO-VERIFY within-turn accumulation from rules-notes.)
            if (player.Power < def.Defense) return SubmitResult.Rejected("Not enough power");

            player.Power -= def.Defense;
            Emit(new ShardsPowerChangedEvent { PlayerIndex = player.Index, Delta = -def.Defense, NewValue = player.Power });

            owner.Champions.Remove(champion);
            champion.Zone = ShardsZone.Discard;
            owner.Discard.Add(champion);
            Emit(new ShardsChampionDestroyedEvent
            {
                OwnerIndex = owner.Index,
                ByPlayerIndex = player.Index,
                InstanceId = champion.InstanceId,
                DefId = champion.DefId
            });
            return SubmitResult.Ok();
        }

        private SubmitResult AttackMonster(ShardsPlayer player, int slotIndex)
        {
            if ((State.Dlc & ShardsDlc.IntoTheHorizon) == 0)
                return SubmitResult.Rejected("Monsters are not in this game");
            if (slotIndex < 0 || slotIndex >= State.CenterRow.Length)
                return SubmitResult.Rejected("Invalid slot");
            var card = State.CenterRow[slotIndex];
            if (card == null || !card.Def.IsMonster) return SubmitResult.Rejected("No monster there");
            var def = card.Def;
            if (player.Power < def.Defense) return SubmitResult.Rejected("Not enough power");

            player.Power -= def.Defense;
            Emit(new ShardsPowerChangedEvent { PlayerIndex = player.Index, Delta = -def.Defense, NewValue = player.Power });

            // Defeated monsters leave the game; the slot refills immediately.
            // TODO-VERIFY monster disposal (removed vs bottom of deck) from rules-notes.
            State.CenterRow[slotIndex] = null;
            card.Zone = ShardsZone.Removed;
            RefillSlot(slotIndex);
            Emit(new ShardsMonsterDefeatedEvent { PlayerIndex = player.Index, SlotIndex = slotIndex, DefId = card.DefId });

            if (def.RewardEffect != null)
                QueueEffect(def.RewardEffect, player.Index, card);
            return SubmitResult.Ok();
        }

        private SubmitResult RecruitRelic(ShardsPlayer player, int instanceId)
        {
            if ((State.Dlc & ShardsDlc.RelicsOfTheFuture) == 0)
                return SubmitResult.Rejected("Relics are not in this game");
            if (player.RelicRecruited) return SubmitResult.Rejected("Relic already recruited this game");
            if (player.Mastery < 10) return SubmitResult.Rejected("Requires Mastery 10");
            var relic = player.SetAside.Find(c => c.InstanceId == instanceId && c.Def.Type == ShardsCardType.Relic);
            if (relic == null) return SubmitResult.Rejected("Not one of your relics");

            player.RelicRecruited = true;
            player.SetAside.RemoveAll(c => c.Def.Type == ShardsCardType.Relic);
            relic.Zone = ShardsZone.Discard;
            player.Discard.Add(relic);
            Emit(new ShardsRelicRecruitedEvent { PlayerIndex = player.Index, DefId = relic.DefId });
            return SubmitResult.Ok();
        }

        private SubmitResult Concede(int playerIndex)
        {
            var player = State.Players[playerIndex];
            if (player.Eliminated) return SubmitResult.Rejected("Already out");
            Emit(new ShardsConcededEvent { PlayerIndex = playerIndex });
            EliminatePlayer(player);
            if (!State.GameOver && State.TurnPlayerIndex == playerIndex && !_endTurnInProgress)
                AdvanceTurn();
            RoutePriority();
            return SubmitResult.Ok();
        }

        // ------------------------------------------------------------------ end turn

        private void BeginEndTurn(ShardsPlayer player)
        {
            _endTurnInProgress = true;

            var opponents = new List<ShardsPlayer>(State.LivingOpponentsOf(player.Index));
            if (player.Power > 0 && opponents.Count > 0)
            {
                if (opponents.Count == 1)
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
                    Min = 0,
                    Max = player.Power,
                    Ordered = true
                };
                foreach (var opponent in opponents)
                    request.Options.Add(new DecisionOption(opponent.Index, opponent.Name));
                // Answer format: one option id per damage point (repeats allowed).
                QueueEffect(new Custom(ctx => SplitDamageFlow(ctx, request)), player.Index, null);
                Pump();
                return;
            }

            FinishEndTurn(player);
        }

        private IEnumerable<ShardsStep> SplitDamageFlow(ShardsContext ctx, DecisionRequest request)
        {
            yield return ShardsStep.AwaitDecision(request);

            _splitTargets = new List<int>();
            _splitAmounts = new List<int>();
            foreach (int optionId in ctx.Answer.ChosenOptionIds)
            {
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
            BeginDefenses(ctx.Controller);
        }

        private void BeginDefenses(ShardsPlayer attacker)
        {
            _pendingDefenses = new Queue<(int, int)>();
            for (int i = 0; i < _splitTargets.Count; i++)
                if (_splitAmounts[i] > 0)
                    _pendingDefenses.Enqueue((_splitTargets[i], _splitAmounts[i]));
            NextDefense(attacker);
        }

        private void NextDefense(ShardsPlayer attacker)
        {
            while (_pendingDefenses != null && _pendingDefenses.Count > 0)
            {
                var (defenderIndex, amount) = _pendingDefenses.Dequeue();
                var defender = State.Players[defenderIndex];
                if (defender.Eliminated) continue;

                // In-play champion shields absorb passively; hand shields are a choice.
                // TODO-VERIFY champion-shield application details from rules-notes (M4).
                int passive = 0;
                foreach (var champion in defender.Champions)
                    passive += champion.Def.Shield;

                var handShields = defender.Hand.FindAll(c => c.Def.Shield > 0);
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
                    var option = new DecisionOption(shield.InstanceId, shield.Def.Name + " (shield " + shield.Def.Shield + ")")
                    {
                        CardInstanceId = shield.InstanceId
                    };
                    request.Options.Add(option);
                }

                int capturedAmount = amount;
                int capturedPassive = passive;
                var capturedDefender = defender;
                QueueEffect(new Custom(ctx => ShieldFlow(ctx, request, attacker.Index, capturedDefender, capturedAmount, capturedPassive)),
                    defender.Index, null);
                Pump();
                return; // resumes via the effect; remaining defenders follow after it
            }

            _pendingDefenses = null;
            FinishEndTurn(State.Players[attacker.Index]);
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
                prevented += card.Def.Shield;
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
            if (defender.Health <= 0)
                EliminatePlayer(defender);
        }

        private void FinishEndTurn(ShardsPlayer player)
        {
            _endTurnInProgress = false;
            _splitTargets = null;
            _splitAmounts = null;

            // Cleanup: play zone → discard, fast-played mercs → bottom of CENTER deck.
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

            // Hand stays (SoI does not discard the hand); draw back up to hand size.
            // TODO-VERIFY: base rules — discard remaining hand at end of turn, or keep?
            // Star-Realms-family games discard + draw 5; rules-notes (M4) settles this.
            foreach (var card in new List<ShardsCard>(player.Hand))
            {
                player.Hand.Remove(card);
                card.Zone = ShardsZone.Discard;
                player.Discard.Add(card);
            }
            int toDraw = State.Rules.HandSize;
            for (int i = 0; i < toDraw; i++)
                DrawOne(player);

            player.ResetTurn();
            Emit(new ShardsCleanupEvent { PlayerIndex = player.Index });

            if (!State.GameOver)
                AdvanceTurn();
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
            State.TurnPlayerIndex = playerIndex;
            var player = State.Players[playerIndex];
            player.CharacterExhausted = false;
            foreach (var champion in player.Champions)
                champion.Exhausted = false;
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
                    if (PendingInput == null || PendingInput.Kind == PendingInputKind.Decision)
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
            // Destiny at Mastery 5 (DLC3) and relic availability are handled as actions/
            // decisions elsewhere; here: elimination + last-survivor.
            if (State.GameOver) return;
            if (State.LivingCount == 1)
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
                if (def.IsMonster)
                {
                    if (player.Power >= def.Defense)
                        actions.Add(new ShardsAttackMonsterAction { PlayerIndex = playerIndex, SlotIndex = s });
                    continue;
                }
                if (player.Gems >= def.Cost)
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

            foreach (var opponent in State.LivingOpponentsOf(playerIndex))
                foreach (var champion in opponent.Champions)
                    if (player.Power >= champion.Def.Defense)
                        actions.Add(new ShardsAttackChampionAction
                        {
                            PlayerIndex = playerIndex,
                            TargetPlayerIndex = opponent.Index,
                            CardInstanceId = champion.InstanceId
                        });

            if ((State.Dlc & ShardsDlc.RelicsOfTheFuture) != 0 && !player.RelicRecruited && player.Mastery >= 10)
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
            // Destiny at Mastery 5 (DLC3): TODO(M5-data) queue the choice when crossing 5
            // once destiny defs exist; acquisition details from rules-notes.
        }

        public void GainHealth(int playerIndex, int amount)
        {
            var player = State.Players[playerIndex];
            int before = player.Health;
            player.Health = System.Math.Min(State.Rules.MaxHealth, player.Health + amount);
            // Note: "would gain at cap" still counts as gaining for conversion effects —
            // effects reading the gain should use the REQUESTED amount, not the delta.
            if (player.Health != before)
                Emit(new ShardsHealthChangedEvent { PlayerIndex = playerIndex, Delta = player.Health - before, NewValue = player.Health });
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
            if (State.CenterDeck.Count == 0) return;
            var card = State.CenterDeck[State.CenterDeck.Count - 1];
            State.CenterDeck.RemoveAt(State.CenterDeck.Count - 1);
            card.Zone = ShardsZone.CenterRow;
            State.CenterRow[slotIndex] = card;
            Emit(new ShardsRowRefilledEvent { SlotIndex = slotIndex, InstanceId = card.InstanceId, DefId = card.DefId });
        }

        private void EliminatePlayer(ShardsPlayer player)
        {
            if (player.Eliminated) return;
            player.Eliminated = true;
            Emit(new ShardsPlayerEliminatedEvent { PlayerIndex = player.Index });
            CheckStateBased();
        }
    }
}
