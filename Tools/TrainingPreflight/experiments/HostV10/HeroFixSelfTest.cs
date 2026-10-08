using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class HeroFixSelfTest
    {
        private static int _checks;
        private static void Check(bool condition, string label)
        { _checks++; if (!condition) throw new InvalidOperationException(label); }
        private static Adapter Fixture(string hero, bool reversed = false)
        {
            var g = new Adapter(78213);
            while (g.Decision?.Context == "soi.herodraft") g.Step(0);
            var p = g.Engine.State.Players[g.Actor];
            p.CharacterId = hero; p.Mastery = 5; p.Gems = 10;
            var deck = g.Engine.State.CenterDeck;
            var a = deck.First(c => c.DefId == "shard_abstractor");
            var b = deck.First(c => c.DefId == "fungal_hermit");
            deck.Remove(a); deck.Remove(b);
            deck.Add(reversed ? a : b); deck.Add(reversed ? b : a);
            Refresh(g); return g;
        }
        private static void Refresh(Adapter g)
        {
            g.Engine.State.InvalidateCardIndex();
            g.Engine.PendingInput.LegalActions = g.Engine.LegalActions(g.Actor);
            g.Rebuild();
        }
        private static int Find(Adapter g, int kind) => Enumerable.Range(0, g.VisibleCount)
            .First(i => g.Visible(i).Kind == kind);
        private static float[] Observation(Adapter g)
        {
            var obs = new float[Encoder.ObsDim];
            Encoder.Encode(g, obs, new float[2048], new float[64]); return obs;
        }
        internal static void Run()
        {
            _checks = 0;
            var ko = Fixture("kosynwu"); var p = ko.Engine.State.Players[ko.Actor];
            int hp = p.Health, log = ko.Engine.Log.Count; long submissions = ko.Submissions;
            ko.Step(Find(ko, 9));
            Check(ko.SacrificePreview && ko.Decision.Context == "soi.banish", "Actual AI preview context");
            Check(p.Health == hp && !p.HeroAbilityUsedThisTurn && ko.Engine.Log.Count == log,
                "Preview cannot pay or mutate real game");
            Check(Observation(ko)[HeroFeatures.Slots[17]] == 1, "Preview is observed");
            ko.Step(Find(ko, 13));
            Check(p.Health == hp && ko.Submissions == submissions && !p.HeroAbilityUsedThisTurn,
                "Decline has no health or game-action cost");
            Check(!ko.Candidates.Any(c => c.Kind == 9), "Repeated same-target decline is suppressed");
            var moved = p.Hand[0]; p.Hand.RemoveAt(0); moved.Zone = ShardsZone.Discard; p.Discard.Add(moved); Refresh(ko);
            Check(ko.Candidates.Any(c => c.Kind == 9), "Changed target zone permits reconsideration");
            ko.Step(Find(ko, 9)); int option = Find(ko, 12); int target = ko.Visible(option).Option.Id;
            ko.Step(option);
            Check(p.Health == hp - 3 && p.HeroAbilityUsedThisTurn, "Commit pays exactly once");
            Check(ko.Engine.State.Banished.Any(c => c.InstanceId == target), "Exactly committed card banished");
            Check(ko.Submissions == submissions + 2, "Both actual engine actions counted");
            var empty = Fixture("kosynwu"); p = empty.Engine.State.Players[empty.Actor]; p.Hand.Clear(); p.Discard.Clear(); Refresh(empty);
            Check(!empty.Candidates.Any(c => c.Kind == 9), "No-target Sacrifice absent from AI menu");

            var a = Fixture("rez"); var b = Fixture("rez", true); int seat = a.Actor;
            a.Step(Find(a, 9)); b.Step(Find(b, 9));
            Check(a.Knowledge.For(seat).SequenceEqual(new[] { "shard_abstractor", "fungal_hermit" }), "Authorized Scry capture");
            Check(a.Knowledge.For(1-seat).Count == 0, "Opponent never receives private reveal");
            a.Step(Find(a, 13)); b.Step(Find(b, 13));
            Check(!Observation(a).SequenceEqual(Observation(b)), "Original post-Scry alias fixed");
            Check(a.Knowledge.For(seat).Count == 2, "Keep-all knowledge survives submission");
            a.Step(Find(a, 8));
            Check(a.Knowledge.For(seat).SequenceEqual(new[] { "fungal_hermit" }), "Reroll consumes top only, preserves next");
            Check(a.Engine.State.CenterRow.Any(c => c?.DefId == "shard_abstractor"), "Reroll reveals predicted top");
            a.Step(Find(a, 8));
            Check(a.Knowledge.For(seat).Count == 0, "Second refill consumes second known card");

            var bury = Fixture("rez"); bury.Step(Find(bury, 9));
            bury.Step(Find(bury, 12)); bury.Step(Find(bury, 13));
            Check(bury.Knowledge.For(bury.Actor).SequenceEqual(new[] { "fungal_hermit" }), "Bury first preserves retained second");
            int before = bury.Engine.State.CenterDeck.Count, mask = CenterKnowledge.FloodMask(bury.Engine);
            int logStart = bury.Engine.Log.Count;
            bury.Engine.State.Players[1].DoomGateFloodUsed = true;
            bury.Engine.ShuffleIngeminexIntoCenterDeck(25);
            bury.Knowledge.AfterSubmit(bury.Engine, logStart, before, mask);
            Check(bury.Knowledge.For(0).Count == 0 && bury.Knowledge.For(1).Count == 0, "Shuffle invalidates both seats");
            Check(HeroFeatures.Slots.Length == 20 && HeroFeatures.Slots.Distinct().Count() == 20 &&
                !HeroFeatures.Slots.Intersect(Encoder.AllegianceSlots).Any(), "Migration slots disjoint");
            Program.Print(new { passed = true, checks = _checks, observationSchema = Encoder.SchemaVersion,
                sacrifice = "preview/decline/empty/target-change/atomic-commit", knowledge = "reveal/seat-privacy/keep/bury/refill/shuffle" });
        }
    }
}
