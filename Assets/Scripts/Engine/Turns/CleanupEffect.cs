using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;

namespace Pascension.Engine.Turns
{
    /// <summary>
    /// End-of-turn cleanup for the turn player:
    /// optional keep-cards (Nyx) → ethereal exile → discard hand → PlayedThisTurn to discard
    /// → clear all pools/marked damage/EOT modifiers → draw back up.
    /// </summary>
    public sealed class CleanupEffect : IEffect
    {
        public IEnumerable<EngineStep> Resolve(EffectContext ctx)
        {
            var p = ctx.Controller;

            // 1. How many cards may be kept (Nyx's Up the Sleeve)?
            int maxKeep = 0;
            foreach (var s in ctx.Api.ActiveStatics(p))
                if (s is ICleanupKeepModifier m)
                    maxKeep = m.MaxKeepCount(ctx.State, p, maxKeep);

            var kept = new List<CardInstance>();
            if (maxKeep > 0 && p.Hand.Count > 0)
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = p.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Keep up to {maxKeep} card(s) in hand",
                    Min = 0,
                    Max = maxKeep
                };
                for (int i = 0; i < p.Hand.Count; i++)
                    req.Options.Add(new DecisionOption(i, p.Hand[i].Def.Name) { CardInstanceId = p.Hand[i].InstanceId });

                yield return EngineStep.AwaitDecision(ctx.Decision(req));

                foreach (int optionId in ctx.Answer.ChosenOptionIds)
                {
                    var option = req.Options.Find(o => o.Id == optionId);
                    if (option == null) continue;
                    var card = ctx.State.FindCard(option.CardInstanceId);
                    if (card != null && card.Zone == ZoneType.Hand && !kept.Contains(card))
                        kept.Add(card);
                }
            }

            // 2. Discard (or exile, for ethereal) the rest of the hand.
            var toDrop = new List<CardInstance>();
            foreach (var card in p.Hand)
                if (!kept.Contains(card))
                    toDrop.Add(card);
            foreach (var card in toDrop)
            {
                bool ethereal = card.Def.HasKeyword(Keyword.Ethereal);
                ctx.Api.MoveCard(card, ethereal ? ZoneType.Exile : ZoneType.Discard, p.Index);
            }

            // 3. Cards played this turn reach the discard pile only now (GDD reshuffle rule).
            var played = new List<CardInstance>(p.PlayedThisTurn);
            foreach (var card in played)
                ctx.Api.MoveCard(card, ZoneType.Discard, p.Index);

            // 4. The turn player's pools expire ("unused action points are lost at the end of
            // YOUR turn" — resources gained off-turn persist until their owner's own turn ends).
            if (p.Ap != 0) ctx.Api.GainAp(p, -p.Ap);
            if (p.DamagePool != 0) ctx.Api.GainDamage(p, -p.DamagePool, fromCardSpell: false);
            foreach (var (_, _, monster) in ctx.State.Market.Monsters())
                monster.MarkedDamage = 0;
            if (ctx.State.Boss != null) ctx.State.Boss.MarkedDamage = 0;
            ctx.State.Continuous.ExpireEndOfTurn();
            ctx.Api.Emit(new TurnDamageClearedEvent());

            // 5. Draw back up (kept cards count toward the total; Arcane Library can raise it).
            int drawTarget = ctx.Rules.HandSize;
            foreach (var s in ctx.Api.ActiveStatics(p))
                if (s is IDrawCountModifier m)
                    drawTarget = m.ModifyDrawCount(ctx.State, p, drawTarget);
            int toDraw = drawTarget - p.Hand.Count;
            if (toDraw > 0)
                ctx.Api.DrawCards(p, toDraw);
        }
    }
}
