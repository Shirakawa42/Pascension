using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;

namespace Pascension.Engine.Effects.Common
{
    public sealed class NullEffect : IEffect
    {
        public static readonly NullEffect Instance = new();

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            yield break;
        }
    }

    /// <summary>Run several effects in sequence within one resolution.</summary>
    public sealed class CompositeEffect : IEffect
    {
        private readonly IEffect[] _parts;

        public CompositeEffect(params IEffect[] parts) => _parts = parts;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            foreach (var part in _parts)
                foreach (var step in part.Resolve(ctx))
                    yield return step;
        }
    }

    public sealed class GainApEffect : IEffect
    {
        private readonly LevelScaledValue _amount;

        public GainApEffect(LevelScaledValue amount) => _amount = amount;
        public GainApEffect(int amount) => _amount = LevelScaledValue.Flat(amount);

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.GainAp(ctx.Scaled(_amount));
            yield break;
        }
    }

    public sealed class GainDamageEffect : IEffect
    {
        private readonly LevelScaledValue _amount;

        public GainDamageEffect(LevelScaledValue amount) => _amount = amount;
        public GainDamageEffect(int amount) => _amount = LevelScaledValue.Flat(amount);

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.GainDamage(ctx.Scaled(_amount));
            yield break;
        }
    }

    public sealed class DrawCardsEffect : IEffect
    {
        private readonly int _count;

        public DrawCardsEffect(int count) => _count = count;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.Draw(_count);
            yield break;
        }
    }

    public sealed class GainXpEffect : IEffect
    {
        private readonly int _amount;

        public GainXpEffect(int amount) => _amount = amount;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.GainXp(_amount);
            yield break;
        }
    }

    /// <summary>Give the first-target monster an HP modifier (e.g. Protective Barrier +3, Hex −2).</summary>
    public sealed class ModifyMonsterHpEffect : IEffect
    {
        private readonly int _amount;
        private readonly ModifierDuration _duration;

        public ModifyMonsterHpEffect(int amount, ModifierDuration duration = ModifierDuration.EndOfTurn)
        {
            _amount = amount;
            _duration = duration;
        }

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            if (ctx.FirstTarget is { } target)
            {
                var monster = ctx.ResolveMonster(target);
                if (monster != null)
                    ctx.AddMonsterHpModifier(monster, _amount, _duration);
            }
            yield break;
        }
    }

    /// <summary>Counter the first-target spell: remove it from the stack, card to PlayedThisTurn.</summary>
    public sealed class CounterTargetSpellEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            if (ctx.FirstTarget is { Kind: Targeting.TargetKind.StackItem } target)
            {
                var item = ctx.State.Stack.Find(target.A);
                if (item != null && item.IsCounterable && !item.Countered)
                {
                    item.Countered = true;
                    ctx.State.Stack.Items.Remove(item);
                    ctx.Api.Emit(new SpellCounteredEvent { StackItemId = item.Id, ByStackItemId = -1 });
                    if (item.SpellCard != null)
                        ctx.Api.MoveCard(item.SpellCard, ZoneType.PlayedThisTurn, item.ControllerIndex);
                }
            }
            yield break;
        }
    }

    /// <summary>Controller chooses cards from their hand to exile (e.g. Ban: exactly 1).</summary>
    public sealed class ExileFromHandEffect : IEffect
    {
        private readonly int _min;
        private readonly int _max;

        public ExileFromHandEffect(int min, int max)
        {
            _min = min;
            _max = max;
        }

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var hand = ctx.Controller.Hand;
            if (hand.Count == 0) yield break;

            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Exile from your hand",
                Min = _min > hand.Count ? hand.Count : _min,
                Max = _max > hand.Count ? hand.Count : _max
            };
            for (int i = 0; i < hand.Count; i++)
                req.Options.Add(new DecisionOption(i, hand[i].Def.Name) { CardInstanceId = hand[i].InstanceId });
            for (int i = 0; i < req.Min; i++)
                req.DefaultOptionIds.Add(i);

            yield return EngineStep.AwaitDecision(ctx.Decision(req));

            var chosen = new List<Cards.CardInstance>();
            foreach (int optionId in ctx.Answer.ChosenOptionIds)
            {
                var option = req.Options.Find(o => o.Id == optionId);
                if (option == null) continue;
                var card = ctx.State.FindCard(option.CardInstanceId);
                if (card != null && card.Zone == ZoneType.Hand)
                    chosen.Add(card);
            }
            foreach (var card in chosen)
                ctx.Api.MoveCard(card, ZoneType.Exile, ctx.ControllerIndex);
        }
    }

    /// <summary>First-target player discards a card at random (Sabotage).</summary>
    public sealed class ForceDiscardRandomEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            if (ctx.FirstTarget is { Kind: Targeting.TargetKind.Player } target)
            {
                var victim = ctx.State.Players[target.A];
                if (victim.Hand.Count > 0)
                {
                    var card = victim.Hand[ctx.State.Rng.Next(victim.Hand.Count)];
                    ctx.Api.MoveCard(card, ZoneType.Discard, victim.Index);
                }
            }
            yield break;
        }
    }

    /// <summary>Grant the controller an extra turn (Time Warp).</summary>
    public sealed class ExtraTurnEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.Controller.PendingExtraTurns++;
            ctx.Api.Emit(new ExtraTurnEvent { PlayerIndex = ctx.ControllerIndex });
            yield break;
        }
    }
}
