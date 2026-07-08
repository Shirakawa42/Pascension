using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Effects
{
    /// <summary>
    /// Everything an effect may see and touch while resolving. Wraps GameApi with the
    /// resolution's controller/source/targets, carries decision answers back into the
    /// iterator, and offers the common shortcuts effects need.
    /// </summary>
    public sealed class EffectContext
    {
        public readonly GameApi Api;
        public readonly int ControllerIndex;
        /// <summary>The resolving spell's card, or the ability's source permanent (may be null).</summary>
        public readonly CardInstance Source;
        public readonly List<TargetRef> Targets;
        /// <summary>True while resolving a card played as a spell (drives Kindle-style modifiers).</summary>
        public readonly bool IsCardSpell;

        /// <summary>The answer to the most recent AwaitDecision yield.</summary>
        public DecisionAnswer Answer;

        public EffectContext(GameApi api, int controllerIndex, CardInstance source, List<TargetRef> targets, bool isCardSpell)
        {
            Api = api;
            ControllerIndex = controllerIndex;
            Source = source;
            Targets = targets ?? new List<TargetRef>();
            IsCardSpell = isCardSpell;
        }

        public GameState State => Api.State;
        public PlayerState Controller => State.Players[ControllerIndex];
        public GameRules Rules => State.Rules;

        public TargetRef? FirstTarget => Targets.Count > 0 ? Targets[0] : null;

        /// <summary>Level-scaled amount for the controller (Redbull/Pyroblast brackets).</summary>
        public int Scaled(LevelScaledValue value) => value.For(Controller.Level);

        // ---- shortcuts ----
        public void GainAp(int n) => Api.GainAp(Controller, n);
        public void GainDamage(int n) => Api.GainDamage(Controller, n, IsCardSpell);
        public void GainXp(int n) => Api.GainXp(Controller, n);
        public void Draw(int n) => Api.DrawCards(Controller, n);
        public void DrawFor(int playerIndex, int n) => Api.DrawCards(State.Players[playerIndex], n);

        /// <summary>Resolve the monster/boss CardInstance a target points at (null if gone).</summary>
        public CardInstance ResolveMonster(TargetRef target)
        {
            if (target.Kind == TargetKind.Boss) return State.Boss;
            if (target.Kind != TargetKind.MonsterSlot) return null;
            var card = State.Market.SlotCard((CardTier)target.A, target.B);
            return card != null && card.Def.IsMonster ? card : null;
        }

        public void AddMonsterHpModifier(CardInstance monster, int amount, ModifierDuration duration)
        {
            State.Continuous.Add(ModifierKind.MonsterHpDelta, monster.InstanceId, amount, duration,
                State.NextTimestamp(), Source?.DefId ?? "effect");
        }

        /// <summary>Cast a card without paying costs, controlled by this effect's controller.
        /// It goes ON TOP of the stack and resolves after the current resolution finishes.
        /// Targets (if the card needs any) must be supplied by the caller.</summary>
        public void CastFree(CardInstance card, List<TargetRef> targets = null)
        {
            Api.PushSpell(card, ControllerIndex, targets, isFree: true);
        }

        /// <summary>Build (and register) a decision to await. Usage: yield return EngineStep.AwaitDecision(ctx.Decision(req));</summary>
        public DecisionRequest Decision(DecisionRequest req) => Api.NewDecision(req);
    }
}
