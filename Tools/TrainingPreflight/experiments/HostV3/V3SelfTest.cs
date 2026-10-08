using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;
using Pascension.Engine.Actions;

namespace Shards.Preflight
{
    internal static class V3SelfTest
    {
        internal static void Run()
        {
            var a = AliasCase(true);
            var b = AliasCase(false);
            var beforeA = Encode(a); var beforeB = Encode(b);
            var slots = new HashSet<int>(Encoder.AllegianceSlots);
            var differences = Enumerable.Range(0, beforeA.Length).Where(i => beforeA[i] != beforeB[i]).ToArray();
            Check(differences.SequenceEqual(new[] { 319, 509 }), "Only Undergrowth and Order distinguish the alias");
            Check(Enumerable.Range(0, beforeA.Length).Where(i => !slots.Contains(i))
                .All(i => beforeA[i] == beforeB[i]), "Every legacy column and action menu is identical");
            Check(Encoder.AllegianceSlots.Select(i => beforeA[i]).SequenceEqual(new[] { 3f, 0f, 1f, 4f, 0f, 0f, 0f }.Select(x => x / 20f)), "Case A exact faction counts");
            Check(Encoder.AllegianceSlots.Select(i => beforeB[i]).SequenceEqual(new[] { 3f, 0f, 2f, 3f, 0f, 0f, 0f }.Select(x => x / 20f)), "Case B exact faction counts");
            int masteryA = PlayAbbot(a), masteryB = PlayAbbot(b);
            Check(masteryA == 9 && masteryB == 8, "Same legal card exposes differing Allegiance effect");
            OpponentPrivacy(0); OpponentPrivacy(1);
            PrismAndYggdrasil();
            int definitions = Encoder.CardIds.Length;
            bool overflowRejected = false;
            ShardsCardDatabase.Register(new ShardsCardDef { Id = "v3_forbidden_definition_190", Quantity = 0 });
            try { Encoder.Initialize(); }
            catch (InvalidOperationException e) { overflowRejected = e.Message.Contains("maximum catalog size is 189"); }
            Check(overflowRejected, "A 190th definition must not overwrite reserved features");
            Program.Print(new { passed = true, observationSchema = Encoder.SchemaVersion, cardDefinitions = definitions,
                featureSlots = Encoder.AllegianceSlots, normalization = 20, fullFloatsCompared = beforeA.Length,
                aliasDistinguishedAt = differences, mainframeMasteryAfter = new[] { masteryA, masteryB },
                opponentHiddenPerturbationSeats = 2, prismAndYggdrasil = true, catalogOverflowRejected = overflowRejected,
                fixtureScope = "Constructed card-conserving starting positions followed by five advertised legal actions; not a full seed reachability proof" });
        }

        private static Adapter AfterDraft(ulong seed)
        {
            var game = new Adapter(seed);
            while (game.Engine.PendingInput.Decision?.Context == "soi.herodraft") game.Step(0);
            return game;
        }

        private static void Refresh(Adapter game)
        {
            game.Engine.State.InvalidateCardIndex();
            game.Engine.PendingInput.LegalActions = game.Engine.LegalActions(game.Actor);
            game.Rebuild();
        }

        private static Adapter AliasCase(bool fastOrder)
        {
            var game = AfterDraft(78213);
            var engine = game.Engine; var state = engine.State; var player = state.TurnPlayer;
            var starter = player.Hand.Concat(player.Deck).Concat(player.Discard).ToList();
            player.Hand.Clear(); player.Deck.Clear(); player.Discard.Clear();
            foreach (var card in starter) { card.Zone = ShardsZone.Banished; state.Banished.Add(card); }
            foreach (var card in starter.Where(c => c.DefId == "crystal").Take(3))
            { state.Banished.Remove(card); card.Zone = ShardsZone.Hand; player.Hand.Add(card); }
            ShardsCard Take(string definition, ShardsZone zone, List<ShardsCard> destination)
            {
                var card = state.CenterDeck.First(c => c.DefId == definition);
                state.CenterDeck.Remove(card); card.Owner = player.Index; card.Zone = zone;
                destination?.Add(card); return card;
            }
            var x = Take("shard_abstractor", fastOrder ? ShardsZone.Deck : ShardsZone.Hand, fastOrder ? player.Deck : player.Hand);
            var y = Take("fungal_hermit", fastOrder ? ShardsZone.Hand : ShardsZone.Deck, fastOrder ? player.Hand : player.Deck);
            Take("mainframe_abbot_duel", ShardsZone.Hand, player.Hand);
            Take("data_heretic_duel", ShardsZone.Discard, player.Discard);
            var temporary = Take(fastOrder ? x.DefId : y.DefId, ShardsZone.CenterRow, null);
            temporary.Owner = -1;
            var restoreRow = state.CenterRow[0]; restoreRow.Zone = ShardsZone.CenterDeck;
            state.CenterDeck.Add(restoreRow); state.CenterRow[0] = temporary;
            player.Mastery = 5; player.ResetTurn(); Refresh(game);
            void Submit(PlayerAction action)
            {
                Check(engine.LegalActions(player.Index).Any(a => a.Describe() == action.Describe()), "Prefix action advertised legal");
                var result = engine.Submit(action);
                Check(result.Accepted && engine.PendingInput.Decision == null, "Prefix action accepted and resolves");
            }
            foreach (var crystal in player.Hand.Where(c => c.DefId == "crystal").ToList())
                Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = crystal.InstanceId });
            if (fastOrder)
            {
                Submit(new ShardsBuyCardAction { PlayerIndex = player.Index, SlotIndex = 0, FastPlay = true });
                Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = y.InstanceId });
            }
            else
            {
                Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = x.InstanceId });
                Submit(new ShardsBuyCardAction { PlayerIndex = player.Index, SlotIndex = 0, FastPlay = true });
            }
            Refresh(game); return game;
        }

        private static int PlayAbbot(Adapter game)
        {
            var p = game.Engine.State.TurnPlayer;
            var card = p.Hand.Single(c => c.DefId == "mainframe_abbot_duel");
            Check(game.Engine.Submit(new ShardsPlayCardAction { PlayerIndex = p.Index, CardInstanceId = card.InstanceId }).Accepted,
                "Mainframe Abbot accepted");
            return p.Mastery;
        }

        private static void OpponentPrivacy(int seat)
        {
            var game = new Adapter((ulong)(600 + seat)); var rng = new Random(7);
            while (game.Actor != seat || game.Engine.PendingInput.Decision != null ||
                game.Engine.State.Players[1 - seat].Hand.Count == 0 || game.Engine.State.Players[1 - seat].Deck.Count == 0)
            {
                Check(!game.Engine.State.GameOver && !game.Truncated, "Reach privacy input");
                game.Step(game.ExerciseChoice(rng));
            }
            var before = Encode(game); var enemy = game.Engine.State.Players[1 - seat];
            Check(enemy.Hand.Count > 0 && enemy.Deck.Count > 0, "Privacy fixture has hidden cards");
            // Change hidden composition, not merely permutation. Own Allegiance
            // and all legacy observations must stay byte-identical.
            enemy.Hand[0].DefId = enemy.Hand[0].DefId == "prism" ? "crystal" : "prism";
            enemy.Deck[0].DefId = enemy.Deck[0].DefId == "fungal_hermit" ? "crystal" : "fungal_hermit";
            enemy.Hand.Reverse(); enemy.Deck.Reverse(); game.Engine.State.CenterDeck.Reverse();
            game.Engine.State.Players[seat].Deck.Reverse(); Refresh(game);
            Check(before.SequenceEqual(Encode(game)), "Opponent hidden membership/order cannot affect own counters");
        }

        private static void PrismAndYggdrasil()
        {
            var game = AfterDraft(888); var p = game.Engine.State.TurnPlayer;
            p.Hand.Clear(); p.Deck.Clear(); p.Discard.Clear(); p.PlayZone.Clear(); p.Champions.Clear(); p.Destinies.Clear();
            ShardsCard Add(string id, ShardsZone zone, List<ShardsCard> list, bool temporary = false)
            {
                var card = new ShardsCard { InstanceId = game.Engine.State.NextInstanceId++, DefId = id,
                    Owner = p.Index, Zone = zone, FastPlayed = temporary };
                list.Add(card); return card;
            }
            Add("prism", ShardsZone.Hand, p.Hand);
            Add("fungal_hermit", ShardsZone.PlayZone, p.PlayZone, true);
            Add("project_yggdrasil", ShardsZone.SetAside, p.Destinies);
            Refresh(game); var encoded = Encode(game);
            var expected = new[] { 0f, 1f, 2f, 1f, 2f, 1f, 0f };
            Check(Encoder.AllegianceSlots.Select(i => encoded[i]).SequenceEqual(expected.Select(x => x / 20f)),
                "Prism and Yggdrasil apply and temporary play counts; destiny itself excluded");
        }

        private static float[] Encode(Adapter game)
        {
            var output = new float[Encoder.ObsDim + Encoder.MaxActions * Encoder.ActionDim + Encoder.MaxActions];
            Encoder.Encode(game, output.AsSpan(0, Encoder.ObsDim), output.AsSpan(Encoder.ObsDim, Encoder.MaxActions * Encoder.ActionDim),
                output.AsSpan(Encoder.ObsDim + Encoder.MaxActions * Encoder.ActionDim, Encoder.MaxActions));
            return output;
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Schema v3 selftest: " + message); }
    }
}
