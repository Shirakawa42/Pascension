using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Board;
using Pascension.Engine.Cards;
using Pascension.Engine.Decisions;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;
using Pascension.Engine.Stack;
using Pascension.Engine.Targeting;
using Pascension.Engine.Turns;

namespace Pascension.Engine.Core
{
    public readonly struct SubmitResult
    {
        public readonly bool Accepted;
        public readonly string Error;

        private SubmitResult(bool accepted, string error)
        {
            Accepted = accepted;
            Error = error;
        }

        public static SubmitResult Ok() => new(true, null);
        public static SubmitResult Rejected(string error) => new(false, error);
    }

    /// <summary>
    /// The sequential rules machine. Exactly one external input is awaited at any time
    /// (see <see cref="PendingInput"/>); all mutation flows through <see cref="Submit"/>,
    /// which validates the action, applies it, then pumps the internal fixpoint loop
    /// (state-based actions → triggers → internal effects → stack/priority → phases)
    /// until input is needed again. Every change lands in the <see cref="EventLog"/>.
    /// </summary>
    public sealed class GameEngine
    {
        public readonly GameState State = new();
        public readonly EventLog Log = new();
        public readonly GameApi Api;

        private readonly ResolutionEngine _resolution = new();

        /// <summary>Consecutive priority passes; doubles as the index into the APNAP order.</summary>
        private int _passCount;
        private bool _cleanupQueued;
        private int _triggerCursor;

        public PendingInput PendingInput { get; private set; }

        public GameEngine(GameConfig config)
        {
            Api = new GameApi(State, Log);
            Setup(config);
            Pump();
        }

        // ------------------------------------------------------------------ setup

        private void Setup(GameConfig config)
        {
            State.Rules = config.Rules;
            State.Rng = new DeterministicRng(config.Seed);

            var heroIds = new List<string>();
            for (int i = 0; i < config.Players.Count; i++)
            {
                var pc = config.Players[i];
                var p = new PlayerState
                {
                    Index = i,
                    Name = pc.Name,
                    HeroId = pc.HeroId,
                    FullControl = pc.FullControl
                };
                State.Players.Add(p);
                heroIds.Add(pc.HeroId);
            }

            Api.Emit(new GameStartedEvent { PlayerCount = config.Players.Count, HeroIds = heroIds });

            // Decks.
            foreach (var p in State.Players)
            {
                var deckSpec = config.Players[p.Index].DeckOverride ?? config.DefaultDeck;
                foreach (var (defId, copies) in deckSpec)
                    for (int c = 0; c < copies; c++)
                        p.Deck.Add(NewCard(defId, p.Index, ZoneType.Deck));
                State.Rng.Shuffle(p.Deck);
            }

            // Market piles + rows.
            BuildPile(CardTier.Basic, config.BasicPile);
            BuildPile(CardTier.Advanced, config.AdvancedPile);
            BuildPile(CardTier.Elite, config.ElitePile);
            for (int t = 0; t < Market.Tiers; t++)
                for (int s = 0; s < State.Rules.MarketRowSize; s++)
                    Api.RefillSlot(Market.TierFromIndex(t), s);

            // Boss.
            if (!string.IsNullOrEmpty(config.BossDefId))
                State.Boss = NewCard(config.BossDefId, -1, ZoneType.Boss);

            // Opening hands + staggered-start compensation.
            for (int i = 0; i < State.Players.Count; i++)
            {
                var p = State.Players[i];
                var (bonusAp, bonusCards) = i < State.Rules.StaggeredStart.Length
                    ? State.Rules.StaggeredStart[i]
                    : (0, 0);
                Api.DrawCards(p, State.Rules.HandSize + bonusCards);
                if (bonusAp > 0)
                    Api.GainAp(p, bonusAp);
            }

            StartTurn(0, incrementRound: false);
        }

        private void BuildPile(CardTier tier, PileSpec spec)
        {
            var pile = State.Market.PileFor(tier);
            foreach (var (defId, copies) in spec.Entries)
                for (int c = 0; c < copies; c++)
                    pile.Add(NewCard(defId, -1, ZoneType.Pile));
            State.Rng.Shuffle(pile);
        }

        private CardInstance NewCard(string defId, int owner, ZoneType zone)
        {
            CardDatabase.Get(defId); // fail fast on unknown ids
            return new CardInstance
            {
                InstanceId = State.NextInstanceId++,
                DefId = defId,
                Owner = owner,
                Zone = zone,
                Timestamp = State.NextTimestamp()
            };
        }

        // ------------------------------------------------------------------ submit

        public SubmitResult Submit(PlayerAction action)
        {
            if (State.GameOver)
                return SubmitResult.Rejected("Game is over");
            if (action == null)
                return SubmitResult.Rejected("Null action");

            if (action is ConcedeAction)
            {
                ExecuteConcede(action.PlayerIndex);
                Pump();
                return SubmitResult.Ok();
            }

            var pending = PendingInput;
            if (pending == null)
                return SubmitResult.Rejected("Engine is not awaiting input");
            if (action.PlayerIndex != pending.PlayerIndex)
                return SubmitResult.Rejected($"Waiting for player {pending.PlayerIndex}, not {action.PlayerIndex}");

            string error;
            if (pending.Kind == PendingInputKind.Decision)
            {
                if (action is not SubmitDecisionAction sda)
                    return SubmitResult.Rejected("A decision answer is required");
                error = ValidateDecision(pending.Decision, sda.Answer);
                if (error != null) return SubmitResult.Rejected(error);
                _resolution.Resume(sda.Answer);
                Pump();
                return SubmitResult.Ok();
            }

            error = ValidateAtPriority(action);
            if (error != null) return SubmitResult.Rejected(error);

            ExecuteAtPriority(action);
            Pump();
            return SubmitResult.Ok();
        }

        private static string ValidateDecision(DecisionRequest req, DecisionAnswer answer)
        {
            if (answer == null || answer.DecisionId != req.Id)
                return "Answer does not match the pending decision";
            if (answer.ChosenOptionIds.Count < req.Min || answer.ChosenOptionIds.Count > req.Max)
                return $"Choose between {req.Min} and {req.Max} options";
            var seen = new List<int>();
            foreach (int id in answer.ChosenOptionIds)
            {
                if (req.Options.Find(o => o.Id == id) == null)
                    return $"Unknown option {id}";
                if (seen.Contains(id))
                    return "Duplicate option";
                seen.Add(id);
            }
            return null;
        }

        // ------------------------------------------------------------------ validation (priority actions)

        private string ValidateAtPriority(PlayerAction action)
        {
            var p = State.Players[action.PlayerIndex];
            bool isTurn = action.PlayerIndex == State.TurnPlayerIndex;
            bool stackEmpty = State.Stack.IsEmpty;
            bool sorceryTiming = isTurn && State.Phase == Phase.Main && stackEmpty;

            switch (action)
            {
                case PassPriorityAction:
                    return null;

                case PlayCardAction play:
                {
                    var card = p.Hand.Find(c => c.InstanceId == play.CardInstanceId);
                    if (card == null) return "Card is not in your hand";
                    var def = card.Def;
                    if (def.IsMonster) return "Monsters cannot be played";
                    if (def.Type != CardType.Instant && !sorceryTiming)
                        return "Only instants can be played now";
                    if (def.SpellTarget != null &&
                        TargetValidator.BuildOptions(State, p.Index, def.SpellTarget).Count == 0)
                        return "No legal targets";
                    return null;
                }

                case BuyCardAction buy:
                {
                    if (!sorceryTiming) return "You can only buy during your main phase with an empty stack";
                    if (buy.TierIndex < 0 || buy.TierIndex >= Market.Tiers) return "Bad tier";
                    var tier = Market.TierFromIndex(buy.TierIndex);
                    if (tier == CardTier.Advanced && p.Level < State.Rules.AdvancedLevelRequirement)
                        return $"Advanced cards require level {State.Rules.AdvancedLevelRequirement}";
                    if (tier == CardTier.Elite && p.Level < State.Rules.EliteLevelRequirement)
                        return $"Elite cards require level {State.Rules.EliteLevelRequirement}";
                    if (buy.SlotIndex < 0 || buy.SlotIndex >= State.Rules.MarketRowSize) return "Bad slot";
                    var card = State.Market.Rows[buy.TierIndex][buy.SlotIndex];
                    if (card == null) return "Slot is empty";
                    if (card.Def.IsMonster) return "Monsters must be killed, not bought";
                    if (p.Ap < Api.GetBuyCost(p, card.Def)) return "Not enough action points";
                    return null;
                }

                case MoveStepsAction move:
                {
                    if (!sorceryTiming) return "You can only move during your main phase with an empty stack";
                    if (move.Steps < 1) return "Move at least 1 step";
                    if (p.Ap < move.Steps) return "Not enough action points";
                    if (p.Position >= State.Rules.BoardSteps) return "Already at the end of the board";
                    return null;
                }

                case AssignDamageAction attack:
                {
                    if (!isTurn) return "You can only attack on your own turn";
                    if (attack.Amount < 1 || attack.Amount > p.DamagePool) return "Invalid damage amount";
                    if (attack.Target.Kind == TargetKind.Boss)
                    {
                        if (State.Boss == null) return "There is no boss";
                        if (p.Position != State.Rules.BoardSteps) return "You must stand on the final step to attack the boss";
                        return null;
                    }
                    if (attack.Target.Kind != TargetKind.MonsterSlot) return "Attack a monster or the boss";
                    var monster = State.Market.SlotCard((CardTier)attack.Target.A, attack.Target.B);
                    if (monster == null || !monster.Def.IsMonster) return "No monster there";
                    return null;
                }

                case ActivateAbilityAction act:
                {
                    if (!sorceryTiming) return "Abilities are used during your main phase with an empty stack";
                    CardInstance source = null;
                    foreach (var c in p.Permanents())
                        if (c.InstanceId == act.SourceInstanceId)
                            source = c;
                    if (source == null) return "You don't control that permanent";
                    if (act.AbilityIndex < 0 || act.AbilityIndex >= source.Def.ActivatedAbilities.Count)
                        return "No such ability";
                    var ability = source.Def.ActivatedAbilities[act.AbilityIndex];
                    if (ability.TapCost && source.Tapped) return "Already tapped";
                    if (p.Ap < ability.ApCost) return "Not enough action points";
                    if (ability.Target != null &&
                        TargetValidator.BuildOptions(State, p.Index, ability.Target).Count == 0)
                        return "No legal targets";
                    if (ability.UsableIf != null && !ability.UsableIf(State, p)) return "Cannot use that now";
                    return null;
                }

                case UseHeroAbilityAction heroAct:
                {
                    if (!sorceryTiming) return "Hero abilities are used during your main phase with an empty stack";
                    var hero = p.Hero;
                    var ability = heroAct.Ultimate ? hero.Ultimate : hero.Active;
                    int unlock = heroAct.Ultimate ? hero.UltimateUnlockLevel : hero.ActiveUnlockLevel;
                    bool used = heroAct.Ultimate ? p.HeroUltimateUsedThisTurn : p.HeroActiveUsedThisTurn;
                    if (ability == null) return "No such hero ability";
                    if (p.Level < unlock) return $"Unlocks at level {unlock}";
                    if (used) return "Already used this turn";
                    if (p.Ap < ability.ApCost) return "Not enough action points";
                    if (ability.Target != null &&
                        TargetValidator.BuildOptions(State, p.Index, ability.Target).Count == 0)
                        return "No legal targets";
                    if (ability.UsableIf != null && !ability.UsableIf(State, p)) return "Cannot use that now";
                    return null;
                }

                default:
                    return $"Unsupported action {action.GetType().Name}";
            }
        }

        // ------------------------------------------------------------------ execution (priority actions)

        private void ExecuteAtPriority(PlayerAction action)
        {
            var p = State.Players[action.PlayerIndex];

            switch (action)
            {
                case PassPriorityAction:
                    if (State.Stack.IsEmpty && State.Phase == Phase.Main && action.PlayerIndex == State.TurnPlayerIndex)
                    {
                        State.Phase = Phase.End;
                        Api.Emit(new PhaseChangedEvent { PlayerIndex = State.TurnPlayerIndex, Phase = Phase.End });
                        ResetPriority();
                    }
                    else
                    {
                        _passCount++;
                    }
                    return;

                case PlayCardAction play:
                {
                    var card = p.Hand.Find(c => c.InstanceId == play.CardInstanceId);
                    if (card.Def.IsManaAbility)
                    {
                        // Mana-ability rule: pure-AP cards never use the stack and cannot be
                        // responded to. The effect resolves via the internal queue (pump step 3),
                        // before any priority window opens.
                        Api.Emit(new CardPlayedEvent { PlayerIndex = p.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                        Api.MoveCard(card, ZoneType.PlayedThisTurn, p.Index);
                        Api.QueueInternal(card.Def.SpellEffect, p.Index, card, $"Play {card.Def.Name}");
                        ResetPriority();
                        return;
                    }
                    if (card.Def.SpellTarget != null)
                    {
                        Api.QueueInternal(new ChooseTargetsAndCastEffect(card), p.Index, card, $"Play {card.Def.Name}");
                    }
                    else
                    {
                        var item = Api.PushSpell(card, p.Index, null, isFree: false);
                        item.TargetSpec = null;
                    }
                    ResetPriority();
                    return;
                }

                case BuyCardAction buy:
                {
                    var card = State.Market.Rows[buy.TierIndex][buy.SlotIndex];
                    int cost = Api.GetBuyCost(p, card.Def);
                    if (cost > 0) Api.GainAp(p, -cost);
                    p.BuysThisTurn++;
                    bool toHand = p.NextBuyToHand;
                    p.NextBuyToHand = false;
                    Api.MoveCard(card, toHand ? ZoneType.Hand : ZoneType.Discard, p.Index);
                    Api.Emit(new CardBoughtEvent
                    {
                        PlayerIndex = p.Index,
                        InstanceId = card.InstanceId,
                        DefId = card.DefId,
                        Tier = Market.TierFromIndex(buy.TierIndex),
                        SlotIndex = buy.SlotIndex,
                        CostPaid = cost
                    });
                    Api.RefillSlot(Market.TierFromIndex(buy.TierIndex), buy.SlotIndex);
                    ResetPriority();
                    return;
                }

                case MoveStepsAction move:
                {
                    int total = BoardSystem.TotalSteps(Api, p, move.Steps);
                    Api.GainAp(p, -move.Steps);
                    BoardSystem.MoveForward(Api, p, total);
                    ResetPriority();
                    return;
                }

                case AssignDamageAction attack:
                {
                    Api.GainDamage(p, -attack.Amount, fromCardSpell: false);
                    string targetName = attack.Target.Kind == TargetKind.Boss
                        ? State.Boss.Def.Name
                        : State.Market.SlotCard((CardTier)attack.Target.A, attack.Target.B).Def.Name;
                    Api.PushAbility(StackItemKind.DamageAssignment, null, p.Index, null,
                        new List<TargetRef> { attack.Target }, $"{attack.Amount} damage → {targetName}", attack.Amount);
                    ResetPriority();
                    return;
                }

                case ActivateAbilityAction act:
                {
                    CardInstance source = null;
                    foreach (var c in p.Permanents())
                        if (c.InstanceId == act.SourceInstanceId)
                            source = c;
                    var ability = source.Def.ActivatedAbilities[act.AbilityIndex];
                    if (ability.TapCost) Api.Tap(source);
                    if (ability.ApCost > 0) Api.GainAp(p, -ability.ApCost);
                    string desc = $"{source.Def.Name}: {ability.Description}";
                    if (ability.ManaAbility)
                    {
                        // Mana-ability rule: resolves immediately, never stacks, no responses.
                        Api.QueueInternal(ability.Effect, p.Index, source, desc);
                    }
                    else if (ability.Target != null)
                        Api.QueueInternal(new ChooseTargetsAndActivateEffect(ability, source, desc), p.Index, source, desc);
                    else
                        Api.PushAbility(StackItemKind.Ability, source, p.Index, ability.Effect, null, desc);
                    ResetPriority();
                    return;
                }

                case UseHeroAbilityAction heroAct:
                {
                    var hero = p.Hero;
                    var ability = heroAct.Ultimate ? hero.Ultimate : hero.Active;
                    if (heroAct.Ultimate) p.HeroUltimateUsedThisTurn = true;
                    else p.HeroActiveUsedThisTurn = true;
                    if (ability.ApCost > 0) Api.GainAp(p, -ability.ApCost);
                    string desc = $"{hero.Name}: {ability.Description}";
                    if (ability.Target != null)
                        Api.QueueInternal(new ChooseTargetsAndActivateEffect(ability, null, desc), p.Index, null, desc);
                    else
                        Api.PushAbility(StackItemKind.Ability, null, p.Index, ability.Effect, null, desc);
                    ResetPriority();
                    return;
                }
            }
        }

        private void ExecuteConcede(int playerIndex)
        {
            var p = State.Players[playerIndex];
            if (p.Conceded) return;
            p.Conceded = true;
            Api.Emit(new PlayerConcededEvent { PlayerIndex = playerIndex });

            // Unblock the engine if we were waiting on this player.
            if (PendingInput != null && PendingInput.PlayerIndex == playerIndex)
            {
                if (PendingInput.Kind == PendingInputKind.Decision)
                {
                    var req = PendingInput.Decision;
                    var answer = new DecisionAnswer { DecisionId = req.Id };
                    answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
                    if (answer.ChosenOptionIds.Count < req.Min)
                        for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                            if (!answer.ChosenOptionIds.Contains(req.Options[i].Id))
                                answer.ChosenOptionIds.Add(req.Options[i].Id);
                    _resolution.Resume(answer);
                }
                else
                {
                    _passCount++;
                }
                PendingInput = null;
            }

            // Last player standing wins.
            int alive = 0, aliveIndex = -1;
            foreach (var pl in State.Players)
                if (!pl.Conceded)
                {
                    alive++;
                    aliveIndex = pl.Index;
                }
            if (alive == 1)
            {
                State.GameOver = true;
                State.WinnerIndex = aliveIndex;
                Api.Emit(new GameEndedEvent { WinnerIndex = aliveIndex, Reason = "All opponents conceded" });
            }
        }

        // ------------------------------------------------------------------ the pump

        private void ResetPriority() => _passCount = 0;

        private List<int> PriorityOrder()
        {
            var order = new List<int>();
            int n = State.Players.Count;
            for (int i = 0; i < n; i++)
            {
                int idx = (State.TurnPlayerIndex + i) % n;
                if (!State.Players[idx].Conceded)
                    order.Add(idx);
            }
            return order;
        }

        private void Pump()
        {
            PendingInput = null;
            int guard = 0;

            while (!State.GameOver)
            {
                if (++guard > 100000)
                    throw new InvalidOperationException("Engine pump did not converge — possible rules loop");

                // 0. A resolution is paused on a decision.
                if (_resolution.Status == ResolutionStatus.AwaitingDecision)
                {
                    PendingInput = PendingInput.ForDecision(_resolution.PendingDecision);
                    return;
                }

                // 1. State-based actions (may end the game).
                StateBasedActions.Run(Api);
                if (State.GameOver) break;

                // 2. Triggered abilities from freshly emitted events.
                if (ScanTriggers())
                    continue;

                // 3. Internal (off-stack) effects: rewards, inn choices, targeting, cleanup.
                if (Api.InternalEffects.Count > 0)
                {
                    var fx = Api.InternalEffects.Dequeue();
                    var ctx = new EffectContext(Api, fx.ControllerIndex, fx.Source, fx.Targets, isCardSpell: false);
                    _resolution.Begin(fx.Effect, ctx);
                    continue;
                }

                // 4. The stack: rotate priority; all-pass resolves the top item.
                if (!State.Stack.IsEmpty)
                {
                    var order = PriorityOrder();
                    if (_passCount >= order.Count)
                    {
                        ResolveTop();
                        ResetPriority();
                        continue;
                    }
                    int holder = order[_passCount];
                    var legal = LegalActionGenerator.Generate(Api, holder);
                    if (!State.Players[holder].FullControl && LegalActionGenerator.OnlyPassAvailable(legal))
                    {
                        _passCount++;
                        continue;
                    }
                    PendingInput = PendingInput.Priority(holder, legal);
                    return;
                }

                // 5. Empty stack: phase machine.
                switch (State.Phase)
                {
                    case Phase.Untap:
                        State.Phase = Phase.Main;
                        Api.Emit(new PhaseChangedEvent { PlayerIndex = State.TurnPlayerIndex, Phase = Phase.Main });
                        ResetPriority();
                        continue;

                    case Phase.Main:
                    {
                        var turnPlayer = State.TurnPlayer;
                        if (turnPlayer.Conceded)
                        {
                            State.Phase = Phase.End;
                            Api.Emit(new PhaseChangedEvent { PlayerIndex = State.TurnPlayerIndex, Phase = Phase.End });
                            ResetPriority();
                            continue;
                        }
                        var legal = LegalActionGenerator.Generate(Api, turnPlayer.Index);
                        PendingInput = PendingInput.Priority(turnPlayer.Index, legal);
                        return;
                    }

                    case Phase.End:
                    {
                        if (_cleanupQueued)
                        {
                            // Cleanup finished (queue drained) — next turn.
                            _cleanupQueued = false;
                            AdvanceTurn();
                            continue;
                        }
                        var order = PriorityOrder();
                        if (_passCount >= order.Count)
                        {
                            _cleanupQueued = true;
                            Api.QueueInternal(new CleanupEffect(), State.TurnPlayerIndex, null, "Cleanup");
                            ResetPriority();
                            continue;
                        }
                        int holder = order[_passCount];
                        var legal = LegalActionGenerator.Generate(Api, holder);
                        if (!State.Players[holder].FullControl && LegalActionGenerator.OnlyPassAvailable(legal))
                        {
                            _passCount++;
                            continue;
                        }
                        PendingInput = PendingInput.Priority(holder, legal);
                        return;
                    }
                }
            }

            PendingInput = null;
        }

        // ------------------------------------------------------------------ stack resolution

        private void ResolveTop()
        {
            var item = State.Stack.Pop();
            if (item.Countered)
                return;
            Api.Emit(new StackResolvedEvent { StackItemId = item.Id });

            var ctx = new EffectContext(Api, item.ControllerIndex,
                item.SpellCard ?? item.SourceCard, item.Targets,
                isCardSpell: item.Kind == StackItemKind.Spell);

            switch (item.Kind)
            {
                case StackItemKind.Spell:
                    _resolution.Begin(new SpellResolutionEffect(item), ctx);
                    break;

                case StackItemKind.DamageAssignment:
                    ApplyDamageAssignment(item);
                    break;

                default: // Ability / Trigger
                    _resolution.Begin(new AbilityResolutionEffect(item), ctx);
                    break;
            }
        }

        private void ApplyDamageAssignment(StackItem item)
        {
            var target = item.Targets[0];
            CardInstance victim = null;
            if (target.Kind == TargetKind.Boss)
            {
                // Attacker may have been moved off step 50 in response.
                if (State.Players[item.ControllerIndex].Position == State.Rules.BoardSteps)
                    victim = State.Boss;
            }
            else if (target.Kind == TargetKind.MonsterSlot)
            {
                var card = State.Market.SlotCard((CardTier)target.A, target.B);
                if (card != null && card.Def.IsMonster)
                    victim = card;
            }

            if (victim == null)
            {
                Api.Emit(new StackFizzledEvent { StackItemId = item.Id });
                return;
            }

            victim.MarkedDamage += item.Amount;
            victim.LastDamagedBy = item.ControllerIndex;
            Api.Emit(new DamageMarkedEvent
            {
                Target = target,
                TargetInstanceId = victim.InstanceId,
                Amount = item.Amount,
                NewMarked = victim.MarkedDamage,
                ByPlayerIndex = item.ControllerIndex
            });
        }

        /// <summary>Runs a spell's effect, then moves the card to its post-resolution zone.</summary>
        private sealed class SpellResolutionEffect : IEffect
        {
            private readonly StackItem _item;

            public SpellResolutionEffect(StackItem item) => _item = item;

            public IEnumerable<EngineStep> Resolve(EffectContext ctx)
            {
                var card = _item.SpellCard;
                var def = card.Def;

                if (Fizzled(ctx, _item))
                {
                    ctx.Api.Emit(new StackFizzledEvent { StackItemId = _item.Id });
                    ctx.Api.MoveCard(card, ZoneType.PlayedThisTurn, _item.ControllerIndex);
                    yield break;
                }

                if (def.SpellEffect != null)
                    foreach (var step in def.SpellEffect.Resolve(ctx))
                        yield return step;

                switch (def.Type)
                {
                    case CardType.Equipment:
                        ctx.Api.Equip(ctx.Controller, card);
                        break;
                    case CardType.Relic:
                        ctx.Api.MoveCard(card, ZoneType.Relics, _item.ControllerIndex);
                        break;
                    default:
                        ctx.Api.MoveCard(card,
                            def.ExileAfterResolve || _item.ExileAfterResolve ? ZoneType.Exile : ZoneType.PlayedThisTurn,
                            _item.ControllerIndex);
                        break;
                }
            }
        }

        /// <summary>Runs an ability/trigger effect with a fizzle check.</summary>
        private sealed class AbilityResolutionEffect : IEffect
        {
            private readonly StackItem _item;

            public AbilityResolutionEffect(StackItem item) => _item = item;

            public IEnumerable<EngineStep> Resolve(EffectContext ctx)
            {
                if (Fizzled(ctx, _item))
                {
                    ctx.Api.Emit(new StackFizzledEvent { StackItemId = _item.Id });
                    yield break;
                }
                if (_item.Effect != null)
                    foreach (var step in _item.Effect.Resolve(ctx))
                        yield return step;
            }
        }

        private static bool Fizzled(EffectContext ctx, StackItem item)
        {
            if (item.TargetSpec == null || item.Targets.Count == 0)
                return false;
            foreach (var t in item.Targets)
                if (TargetValidator.IsStillValid(ctx.State, t, item.TargetSpec))
                    return false;
            return true;
        }

        // ------------------------------------------------------------------ triggers

        /// <summary>Scan events emitted since the last scan; push matching triggers (APNAP). Returns true if any pushed.</summary>
        private bool ScanTriggers()
        {
            if (_triggerCursor >= Log.Count) return false;
            int from = _triggerCursor;
            _triggerCursor = Log.Count;

            bool pushed = false;
            var order = PriorityOrder();
            for (int i = from; i < Log.Count; i++)
            {
                var e = Log[i];
                foreach (int playerIndex in order)
                {
                    var p = State.Players[playerIndex];
                    foreach (var (source, ability) in Api.ActiveTriggers(p))
                    {
                        if (ability.Filter(e, source))
                        {
                            var item = Api.PushAbility(StackItemKind.Trigger, source.SourceCard, playerIndex,
                                ability.Effect, null, ability.Description);
                            item.TargetSpec = null;
                            pushed = true;
                        }
                    }
                }
            }
            if (pushed) ResetPriority();
            return pushed;
        }

        // ------------------------------------------------------------------ turns

        private void StartTurn(int playerIndex, bool incrementRound)
        {
            State.TurnPlayerIndex = playerIndex;
            if (incrementRound) State.Round++;
            var p = State.Players[playerIndex];

            p.BuysThisTurn = 0;
            p.MovesThisTurn = 0;
            p.DamageCardsThisTurn = 0;
            p.HeroActiveUsedThisTurn = false;
            p.HeroUltimateUsedThisTurn = false;
            p.NextBuyToHand = false;

            State.Phase = Phase.Untap;
            Api.Emit(new TurnStartedEvent { PlayerIndex = playerIndex, Round = State.Round });

            bool anyUntapped = false;
            foreach (var card in p.Permanents())
            {
                if (card.Tapped)
                {
                    card.Tapped = false;
                    anyUntapped = true;
                }
            }
            if (anyUntapped)
                Api.Emit(new PermanentsUntappedEvent { PlayerIndex = playerIndex });
        }

        private void AdvanceTurn()
        {
            var current = State.TurnPlayer;
            if (current.PendingExtraTurns > 0 && !current.Conceded)
            {
                current.PendingExtraTurns--;
                StartTurn(current.Index, incrementRound: false);
                return;
            }

            int n = State.Players.Count;
            for (int i = 1; i <= n; i++)
            {
                int next = (State.TurnPlayerIndex + i) % n;
                if (!State.Players[next].Conceded)
                {
                    StartTurn(next, incrementRound: next <= State.TurnPlayerIndex);
                    return;
                }
            }
        }
    }
}
