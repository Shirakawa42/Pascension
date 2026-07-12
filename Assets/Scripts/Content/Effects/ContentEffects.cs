using System.Collections.Generic;
using Pascension.Engine.Board;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;
using Pascension.Engine.Targeting;

namespace Pascension.Content.Effects
{
    /// <summary>Move the controller forward for free (Portal Stone, Dash, Blitz, Traveler's Map).</summary>
    public sealed class FreeMoveEffect : IEffect
    {
        private readonly int _steps;

        public FreeMoveEffect(int steps) => _steps = steps;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            BoardSystem.MoveForward(ctx.Api, ctx.Controller, _steps, countsAsMove: false);
            yield break;
        }
    }

    /// <summary>Cornelius's Trade: discard a card from your hand, gain 2 AP.</summary>
    public sealed class TradeEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var hand = ctx.Controller.Hand;
            if (hand.Count == 0) yield break;

            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Discard a card to gain 2 AP",
                Min = 1,
                Max = 1
            };
            for (int i = 0; i < hand.Count; i++)
                req.Options.Add(new DecisionOption(i, hand[i].Def.Name) { CardInstanceId = hand[i].InstanceId });
            req.DefaultOptionIds.Add(0);

            yield return EngineStep.AwaitDecision(ctx.Decision(req));

            foreach (int optionId in ctx.Answer.ChosenOptionIds)
            {
                var option = req.Options.Find(o => o.Id == optionId);
                var card = option == null ? null : ctx.State.FindCard(option.CardInstanceId);
                if (card != null && card.Zone == ZoneType.Hand)
                {
                    ctx.Api.MoveCard(card, ZoneType.Discard, ctx.ControllerIndex);
                    ctx.GainAp(2);
                }
            }
        }
    }

    /// <summary>Cornelius's Express Delivery: your next buy this turn goes to your hand.</summary>
    public sealed class ExpressDeliveryEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            ctx.Controller.NextBuyToHand = true;
            yield break;
        }
    }

    /// <summary>Mimic reward: put the top card of the advanced pile into the killer's discard for free.</summary>
    public sealed class MimicRewardEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var pile = ctx.State.Market.PileFor(CardTier.Advanced);
            if (pile.Count > 0)
            {
                var card = pile[0];
                ctx.Api.MoveCard(card, ZoneType.Discard, ctx.ControllerIndex);
            }
            yield break;
        }
    }

    /// <summary>Lich reward: exile up to 3 cards from your discard pile.</summary>
    public sealed class ExileFromDiscardEffect : IEffect
    {
        private readonly int _max;

        public ExileFromDiscardEffect(int max) => _max = max;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var discard = ctx.Controller.Discard;
            if (discard.Count == 0) yield break;

            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Exile up to {_max} cards from your discard",
                Min = 0,
                Max = _max < discard.Count ? _max : discard.Count
            };
            for (int i = 0; i < discard.Count; i++)
                req.Options.Add(new DecisionOption(i, discard[i].Def.Name) { CardInstanceId = discard[i].InstanceId });

            yield return EngineStep.AwaitDecision(ctx.Decision(req));

            var chosen = new List<CardInstance>();
            foreach (int optionId in ctx.Answer.ChosenOptionIds)
            {
                var option = req.Options.Find(o => o.Id == optionId);
                var card = option == null ? null : ctx.State.FindCard(option.CardInstanceId);
                if (card != null && card.Zone == ZoneType.Discard)
                    chosen.Add(card);
            }
            foreach (var card in chosen)
                ctx.Api.MoveCard(card, ZoneType.Exile, ctx.ControllerIndex);
        }
    }

    /// <summary>Cataclysm: exile every face-up market card, gain 1 XP per monster exiled, refill all slots.</summary>
    public sealed class CataclysmEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            int monsters = 0;
            for (int t = 0; t < Market.Tiers; t++)
            {
                var row = ctx.State.Market.Rows[t];
                for (int s = 0; s < row.Length; s++)
                {
                    var card = row[s];
                    if (card == null) continue;
                    if (card.Def.IsMonster) monsters++;
                    ctx.Api.ExileFromMarket(card);
                }
            }
            if (monsters > 0)
                ctx.GainXp(monsters);
            for (int t = 0; t < Market.Tiers; t++)
                for (int s = 0; s < ctx.State.Market.Rows[t].Length; s++)
                    ctx.Api.RefillSlot(Market.TierFromIndex(t), s);
            yield break;
        }
    }

    /// <summary>Blink: return target relic or equipment to its owner's discard pile.</summary>
    public sealed class ReturnPermanentToDiscardEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            if (ctx.FirstTarget is { Kind: TargetKind.Card } target)
            {
                var card = ctx.State.FindCard(target.A);
                if (card != null && (card.Zone == ZoneType.Equipment || card.Zone == ZoneType.Relics))
                    ctx.Api.MoveCard(card, ZoneType.Discard, card.Owner);
            }
            yield break;
        }
    }

    /// <summary>
    /// Mind Steal: counter target spell; if it was an instant, you may cast a copy for free
    /// (the copy is exiled after it resolves).
    /// </summary>
    public sealed class MindStealEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            if (ctx.FirstTarget is not { Kind: TargetKind.StackItem } target)
                yield break;
            var item = ctx.State.Stack.Find(target.A);
            if (item == null || !item.IsCounterable || item.Countered)
                yield break;

            var counteredDef = item.SpellCard.Def;

            item.Countered = true;
            ctx.State.Stack.Items.Remove(item);
            ctx.Api.Emit(new SpellCounteredEvent { StackItemId = item.Id, ByStackItemId = -1 });
            ctx.Api.MoveCard(item.SpellCard, ZoneType.PlayedThisTurn, item.ControllerIndex);

            if (counteredDef.Type != CardType.Instant)
                yield break;

            // Optionally cast a copy.
            List<TargetRef> copyTargets = null;
            if (counteredDef.SpellTarget != null)
            {
                var options = TargetValidator.BuildOptions(ctx.State, ctx.ControllerIndex, counteredDef.SpellTarget);
                if (options.Count == 0)
                    yield break; // copy would have no targets — cannot cast
                var yn = DecisionRequest.YesNo(ctx.ControllerIndex, $"Cast a copy of {counteredDef.Name} for free?");
                yield return EngineStep.AwaitDecision(ctx.Decision(yn));
                if (!ctx.Answer.IsYes) yield break;

                var targetReq = ChooseTargetsAndCastEffect.BuildTargetDecision(ctx, counteredDef.SpellTarget, options);
                yield return EngineStep.AwaitDecision(ctx.Decision(targetReq));
                copyTargets = ChooseTargetsAndCastEffect.ExtractTargets(ctx.Answer, targetReq);
            }
            else
            {
                var yn = DecisionRequest.YesNo(ctx.ControllerIndex, $"Cast a copy of {counteredDef.Name} for free?");
                yield return EngineStep.AwaitDecision(ctx.Decision(yn));
                if (!ctx.Answer.IsYes) yield break;
            }

            var copy = new CardInstance
            {
                InstanceId = ctx.State.NextInstanceId++,
                DefId = counteredDef.Id,
                Owner = ctx.ControllerIndex,
                Zone = ZoneType.Exile, // transient — pushed to the stack immediately
                Timestamp = ctx.State.NextTimestamp()
            };
            ctx.State.MarketExile.Add(copy);
            var pushed = ctx.Api.PushSpell(copy, ctx.ControllerIndex, copyTargets, isFree: true);
            pushed.TargetSpec = counteredDef.SpellTarget;
            pushed.ExileAfterResolve = true;
        }
    }

    /// <summary>
    /// Random Bullshit Go: exile cards from the top of the advanced pile until two instants
    /// (not named Random Bullshit Go) are exiled. You may cast them for free (you keep cast
    /// cards). The other cards exiled this way go to the bottom of the pile in any order.
    /// </summary>
    public sealed class RandomBullshitGoEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var pile = ctx.State.Market.PileFor(CardTier.Advanced);
            var exiled = new List<CardInstance>();
            var instants = new List<CardInstance>();

            while (instants.Count < 2 && pile.Count > 0)
            {
                var card = pile[0];
                ctx.Api.ExileFromMarket(card);
                exiled.Add(card);
                if (card.Def.Type == CardType.Instant && card.Def.Id != "random_bullshit_go")
                    instants.Add(card);
            }

            foreach (var instant in instants)
            {
                var def = instant.Def;
                List<TargetRef> targets = null;

                if (def.SpellTarget != null)
                {
                    var options = TargetValidator.BuildOptions(ctx.State, ctx.ControllerIndex, def.SpellTarget);
                    if (options.Count == 0)
                        continue; // no legal targets — cannot cast it
                    var yn = DecisionRequest.YesNo(ctx.ControllerIndex, $"Cast {def.Name} for free?");
                    yield return EngineStep.AwaitDecision(ctx.Decision(yn));
                    if (!ctx.Answer.IsYes) continue;

                    var targetReq = ChooseTargetsAndCastEffect.BuildTargetDecision(ctx, def.SpellTarget, options);
                    yield return EngineStep.AwaitDecision(ctx.Decision(targetReq));
                    targets = ChooseTargetsAndCastEffect.ExtractTargets(ctx.Answer, targetReq);
                }
                else
                {
                    var yn = DecisionRequest.YesNo(ctx.ControllerIndex, $"Cast {def.Name} for free?");
                    yield return EngineStep.AwaitDecision(ctx.Decision(yn));
                    if (!ctx.Answer.IsYes) continue;
                }

                exiled.Remove(instant);
                var pushed = ctx.Api.PushSpell(instant, ctx.ControllerIndex, targets, isFree: true);
                pushed.TargetSpec = def.SpellTarget;
            }

            if (exiled.Count == 0) yield break;

            // Bottom the rest in an order chosen by the controller.
            if (exiled.Count > 1)
            {
                var order = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.OrderCards,
                    Title = "Put these on the bottom of the advanced pile (top of list goes first)",
                    Min = exiled.Count,
                    Max = exiled.Count,
                    Ordered = true
                };
                for (int i = 0; i < exiled.Count; i++)
                {
                    order.Options.Add(new DecisionOption(i, exiled[i].Def.Name)
                    {
                        CardInstanceId = exiled[i].InstanceId,
                        DefId = exiled[i].DefId // exiled to the market pile — not in any snapshot zone
                    });
                    order.DefaultOptionIds.Add(i);
                }

                yield return EngineStep.AwaitDecision(ctx.Decision(order));

                var sorted = new List<CardInstance>();
                foreach (int optionId in ctx.Answer.ChosenOptionIds)
                {
                    var option = order.Options.Find(o => o.Id == optionId);
                    var card = option == null ? null : exiled.Find(c => c.InstanceId == option.CardInstanceId);
                    if (card != null && !sorted.Contains(card))
                        sorted.Add(card);
                }
                foreach (var card in exiled)
                    if (!sorted.Contains(card))
                        sorted.Add(card);
                exiled = sorted;
            }

            foreach (var card in exiled)
                ctx.Api.BottomOfPile(card, CardTier.Advanced);
        }
    }
}
