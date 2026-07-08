using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Decisions;
using Pascension.Engine.Stack;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Effects
{
    /// <summary>
    /// Internal effect: ask the controller to choose targets for a card they are playing,
    /// then push the spell onto the stack. Used whenever a played card has a TargetSpec.
    /// </summary>
    public sealed class ChooseTargetsAndCastEffect : IEffect
    {
        private readonly CardInstance _card;

        public ChooseTargetsAndCastEffect(CardInstance card) => _card = card;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var spec = _card.Def.SpellTarget;
            var options = TargetValidator.BuildOptions(ctx.State, ctx.ControllerIndex, spec);
            if (options.Count == 0)
                yield break; // targets vanished between submit and resolution — the play simply aborts

            var req = BuildTargetDecision(ctx, spec, options);
            yield return EngineStep.AwaitDecision(ctx.Decision(req));

            var targets = ExtractTargets(ctx.Answer, req);
            var item = ctx.Api.PushSpell(_card, ctx.ControllerIndex, targets, isFree: false);
            item.TargetSpec = spec;
        }

        internal static DecisionRequest BuildTargetDecision(EffectContext ctx, TargetSpec spec, List<TargetRef> options)
        {
            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseTargets,
                Title = spec.Description,
                Min = 1,
                Max = 1
            };
            for (int i = 0; i < options.Count; i++)
                req.Options.Add(new DecisionOption(i, DescribeTarget(ctx, options[i])) { Target = options[i] });
            req.DefaultOptionIds.Add(0);
            return req;
        }

        internal static List<TargetRef> ExtractTargets(DecisionAnswer answer, DecisionRequest req)
        {
            var targets = new List<TargetRef>();
            foreach (int optionId in answer.ChosenOptionIds)
            {
                var option = req.Options.Find(o => o.Id == optionId);
                if (option?.Target != null)
                    targets.Add(option.Target.Value);
            }
            return targets;
        }

        internal static string DescribeTarget(EffectContext ctx, TargetRef target)
        {
            switch (target.Kind)
            {
                case TargetKind.MonsterSlot:
                    var monster = ctx.ResolveMonster(target);
                    return monster != null ? monster.Def.Name : "monster";
                case TargetKind.Boss:
                    return ctx.State.Boss?.Def.Name ?? "the boss";
                case TargetKind.Player:
                    return ctx.State.Players[target.A].Name;
                case TargetKind.StackItem:
                    return ctx.State.Stack.Find(target.A)?.Description ?? "spell";
                case TargetKind.Card:
                    return ctx.State.FindCard(target.A)?.Def.Name ?? "card";
                default:
                    return target.ToString();
            }
        }
    }

    /// <summary>
    /// Internal effect: choose targets for an already-paid activated ability
    /// (equipment tap / hero active), then push it onto the stack.
    /// </summary>
    public sealed class ChooseTargetsAndActivateEffect : IEffect
    {
        private readonly ActivatedAbility _ability;
        private readonly CardInstance _source;
        private readonly string _description;

        public ChooseTargetsAndActivateEffect(ActivatedAbility ability, CardInstance source, string description)
        {
            _ability = ability;
            _source = source;
            _description = description;
        }

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var targets = new List<TargetRef>();
            if (_ability.Target != null)
            {
                var options = TargetValidator.BuildOptions(ctx.State, ctx.ControllerIndex, _ability.Target);
                if (options.Count == 0)
                    yield break; // cost already paid, targets gone — ability aborts (rare)
                var req = ChooseTargetsAndCastEffect.BuildTargetDecision(ctx, _ability.Target, options);
                yield return EngineStep.AwaitDecision(ctx.Decision(req));
                targets = ChooseTargetsAndCastEffect.ExtractTargets(ctx.Answer, req);
            }

            var item = ctx.Api.PushAbility(StackItemKind.Ability, _source, ctx.ControllerIndex, _ability.Effect, targets, _description);
            item.TargetSpec = _ability.Target;
        }
    }
}
