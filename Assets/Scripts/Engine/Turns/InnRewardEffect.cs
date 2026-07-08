using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Effects;

namespace Pascension.Engine.Turns
{
    /// <summary>
    /// Inn reward: choose 1 option (2 with Wren's Trailblazer) of
    /// +2 XP / draw 2 cards / exile up to 2 cards from your discard.
    /// </summary>
    public sealed class InnRewardEffect : IEffect
    {
        private const int OptXp = 0;
        private const int OptDraw = 1;
        private const int OptThin = 2;

        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            int picks = 1;
            foreach (var s in ctx.Api.ActiveStatics(ctx.Controller))
                if (s is IInnChoiceCountModifier m)
                    picks = m.ModifyChoiceCount(ctx.State, ctx.Controller, picks);

            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.InnChoice,
                Title = "Inn reward — rest and recover",
                Min = 1,
                Max = picks
            };
            req.Options.Add(new DecisionOption(OptXp, "Gain 2 XP"));
            req.Options.Add(new DecisionOption(OptDraw, "Draw 2 cards"));
            req.Options.Add(new DecisionOption(OptThin, "Exile up to 2 cards from your discard pile"));
            req.DefaultOptionIds.Add(OptXp);

            yield return EngineStep.AwaitDecision(ctx.Decision(req));

            var picksChosen = new List<int>(ctx.Answer.ChosenOptionIds);
            foreach (int choice in picksChosen)
            {
                switch (choice)
                {
                    case OptXp:
                        ctx.GainXp(2);
                        break;
                    case OptDraw:
                        ctx.Draw(2);
                        break;
                    case OptThin:
                    {
                        var discard = ctx.Controller.Discard;
                        if (discard.Count == 0) break;
                        var thin = new DecisionRequest
                        {
                            PlayerIndex = ctx.ControllerIndex,
                            Kind = DecisionKind.ChooseCards,
                            Title = "Exile up to 2 cards from your discard",
                            Min = 0,
                            Max = discard.Count < 2 ? discard.Count : 2
                        };
                        for (int i = 0; i < discard.Count; i++)
                            thin.Options.Add(new DecisionOption(i, discard[i].Def.Name) { CardInstanceId = discard[i].InstanceId });

                        yield return EngineStep.AwaitDecision(ctx.Decision(thin));

                        var chosen = new List<Cards.CardInstance>();
                        foreach (int optionId in ctx.Answer.ChosenOptionIds)
                        {
                            var option = thin.Options.Find(o => o.Id == optionId);
                            if (option == null) continue;
                            var card = ctx.State.FindCard(option.CardInstanceId);
                            if (card != null && card.Zone == ZoneType.Discard)
                                chosen.Add(card);
                        }
                        foreach (var card in chosen)
                            ctx.Api.MoveCard(card, ZoneType.Exile, ctx.ControllerIndex);
                        break;
                    }
                }
            }
        }
    }
}
