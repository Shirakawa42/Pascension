using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Shadow of Salvation (DLC 2), competitive content only: 12 center cards
    /// (3 errata Cloud Oracles + 9 Aion cards), Rez as a 5th character with his two
    /// relics. The co-op campaign is out of scope.</summary>
    public static class ShardsShadowSet
    {
        private const string SET = "shadow_of_salvation";
        private const ShardsFaction A = ShardsFaction.Aion;

        public static void Register()
        {
            // Errata reprint of RotF's Cloud Oracles ("enemy players" wording — identical
            // in PvP). When this set is enabled the engine skips the RotF copies.
            SoiCard.New("cloud_oracles_sos", "Cloud Oracles").InSet(SET).Faction(ShardsFaction.Order)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(E.Draw(1), new If(HighestMastery, E.Gems(2))))
                .Text("Draw a card. If your mastery is higher than every enemy player's, gain 2 gems.")
                .Art("floating oracles conferring inside a storm cloud of data").Register();

            var swyft = SoiCard.New("swyft", "Swyft").InSet(SET).Faction(A)
                .Type(ShardsCardType.Champion).Cost(5).Qty(2).Defense(3)
                .Exhausts(E.Mix(gems: 2, power: 2))
                .Text("Exhaust: gain 2 gems and 2 power. If your character is Rez, you may keep cards you fast-play (they join your discard).")
                .Art("a lithe time-runner leaving afterimages of light mid-sprint");
            swyft.Def.KeepFastPlaysCharacter = "rez";
            swyft.Register();

            var breaker = SoiCard.New("breaker", "Breaker").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(6).Qty(1).Shield(4)
                .Plays(new WarpUpTo(-1))
                .Text("Shield 4. When you recruit this, it goes to your hand instead of your discard pile. Warp: fast-play any ally from the row for free.")
                .Art("a heavyset dimension-breaker punching a rift open bare-handed");
            breaker.Def.RecruitsToHand = true;
            breaker.Register();

            SoiCard.New("brute", "Brute").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(2)
                .Plays(E.Seq(E.Power(4), new WarpUpTo(2)))
                .Text("Shield 2. Gain 4 power. Warp 2: fast-play an ally costing 2 or less for free.")
                .Art("a bruiser wreathed in unstable warp energy cracking knuckles").Register();

            SoiCard.New("dash", "Dash").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(2).Qty(2).Shield(2)
                .Plays(E.Seq(new Custom(DashFlow), E.Draw(1)))
                .Text("Shield 2. You may put an Aion card from your discard pile on top of your deck. Draw a card.")
                .Art("a blur of motion threading between frozen raindrops").Register();

            SoiCard.New("lucky", "Lucky").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(4).Qty(2).Shield(2)
                .Plays(E.Seq(E.Gems(2), new WarpUpTo(3)))
                .Text("Shield 2. Gain 2 gems. Warp 3: fast-play an ally costing 3 or less for free.")
                .Art("a grinning gambler flipping a coin that lands on its edge").Register();

            // ---- Rez's relics (normal relic rules; need DLC1 to be recruitable) ----

            SoiCard.New("slipstream_shard", "Slipstream Shard").InSet(SET).Faction(A)
                .Type(ShardsCardType.Relic).Character("rez").Qty(1)
                .Plays(E.Seq(E.Mix(mastery: 1, draw: 1), E.At(20, new Custom(ExtraTurn))))
                .Text("Gain 1 mastery and draw a card. M20: take an extra turn (once per game).")
                .Art("a sliver of crystallized time bending light around it").Register();

            SoiCard.New("warpquartz", "Warpquartz").InSet(SET).Faction(A)
                .Type(ShardsCardType.Relic).Character("rez").Qty(1)
                .Plays(E.Seq(E.Mix(gems: 3, power: 3), E.At(20, new Custom(WarpquartzBanish))))
                .Text("Gain 3 gems and 3 power. M20: banish up to 3 allies from your hand/discard and gain their effects.")
                .Art("a jagged quartz cluster humming with compressed warp energy").Register();
        }

        private static bool HighestMastery(ShardsContext ctx)
        {
            foreach (var other in ctx.Engine.State.Players)
                if (other.Index != ctx.ControllerIndex && !other.Eliminated &&
                    other.Mastery >= ctx.Controller.Mastery)
                    return false;
            return true;
        }

        private static IEnumerable<ShardsStep> DashFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var candidates = player.Discard.FindAll(c => c.Def.Faction == A);
            if (candidates.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Put an Aion card from your discard pile on top of your deck?",
                Context = "soi.return",
                Min = 0,
                Max = 1
            };
            foreach (var card in candidates)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = player.Discard.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen == null) yield break;
            player.Discard.Remove(chosen);
            chosen.Zone = ShardsZone.Deck;
            player.Deck.Add(chosen); // list end = top
            ctx.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = chosen.InstanceId, DefId = chosen.DefId });
        }

        private static IEnumerable<ShardsStep> ExtraTurn(ShardsContext ctx)
        {
            var player = ctx.Controller;
            if (player.ExtraTurnUsed) yield break;
            player.ExtraTurnUsed = true;
            ctx.Engine.State.ExtraTurnForPlayer = player.Index;
        }

        private static IEnumerable<ShardsStep> WarpquartzBanish(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var candidates = new List<ShardsCard>();
            foreach (var card in player.Hand)
                if (!card.Def.IsChampion)
                    candidates.Add(card);
            foreach (var card in player.Discard)
                if (!card.Def.IsChampion)
                    candidates.Add(card);
            if (candidates.Count == 0) yield break;

            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Banish up to 3 allies from your hand/discard and gain their effects",
                Context = "soi.banish",
                Min = 0,
                Max = 3
            };
            foreach (var card in candidates)
            {
                string zone = card.Zone == ShardsZone.Hand ? "hand" : "discard";
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name + " (" + zone + ")") { CardInstanceId = card.InstanceId, DefId = card.DefId });
            }
            yield return ShardsStep.AwaitDecision(request);

            var chosen = new List<ShardsCard>();
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var card = candidates.Find(c => c.InstanceId == id);
                if (card != null) chosen.Add(card);
            }
            foreach (var card in chosen)
                ctx.Engine.Banish(card, card.Zone == ShardsZone.Hand ? player.Hand : player.Discard);
            foreach (var card in chosen)
                if (card.Def.PlayEffect != null)
                    foreach (var step in card.Def.PlayEffect.Resolve(ctx))
                        yield return step;
        }
    }
}
