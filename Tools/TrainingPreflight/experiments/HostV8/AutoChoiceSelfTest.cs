using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class AutoChoiceSelfTest
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int _checks;
        private static void Check(bool value, string label)
        { _checks++; if (!value) throw new InvalidOperationException("Auto choice: " + label); }
        private static (Adapter game, ShardsCard source) Fixture(string sourceKind, int copies = 1, bool alreadyBanished = false)
        {
            var g = new Adapter(826123);
            while (g.Decision?.Context == "soi.herodraft") g.Step(0);
            var p = g.Engine.State.Players[g.Actor]; var enemy = g.Engine.State.Players[1 - p.Index];
            p.Hand.Clear(); p.PlayZone.Clear(); p.ResetTurn(); p.Gems = 0;
            var source = new ShardsCard { InstanceId = g.Engine.State.NextInstanceId++, Owner = p.Index,
                DefId = sourceKind == "physical" ? "reactor_drone_duel" : "ojas_genesis_druid",
                Zone = ShardsZone.PlayZone, BanishAtCleanup = alreadyBanished };
            if (sourceKind == "hidden")
            { source.Owner = enemy.Index; source.Zone = ShardsZone.Hand; enemy.Hand.Add(source); }
            else if (sourceKind != "null") p.PlayZone.Add(source);
            else source = null;
            g.Engine.State.InvalidateCardIndex();
            ShardsCardDatabase.TryGet("reactor_drone_duel", out var drone);
            for (int i = 0; i < copies; i++)
                typeof(ShardsEngine).GetMethod("QueueEffect", Private).Invoke(g.Engine,
                    new object[] { drone.PlayEffect, p.Index, source });
            typeof(ShardsEngine).GetMethod("Pump", Private).Invoke(g.Engine, null);
            Check(g.Decision?.Context == "soi.mode", "Real Reactor effect parks before adapter rebuild");
            return (g, source);
        }
        private static void Manual(string sourceKind, bool alreadyBanished = false)
        {
            var (g, source) = Fixture(sourceKind, alreadyBanished: alreadyBanished);
            int seat = g.Actor; long submissions = g.Submissions;
            g.Rebuild();
            Check(g.Decision?.Context == "soi.mode" && g.VisibleCount == 2,
                sourceKind + " keeps both legal modes");
            Check(g.Submissions == submissions && g.Engine.State.Players[seat].Gems == 0,
                sourceKind + " does not auto-submit");
            g.Step(Enumerable.Range(0, g.VisibleCount).Single(i => g.Visible(i).Option.Id == 2));
            Check(g.Engine.State.Players[seat].Gems == 3, "Manual high mode uses normal engine effect");
            if (sourceKind == "physical") Check(source.BanishAtCleanup, "Physical high mode preserves banish cost");
        }
        internal static void Run()
        {
            _checks = 0;
            Manual("physical"); Manual("physical", true); Manual("hidden"); Manual("null");
            foreach (int count in new[] { 1, 3, 128 })
            {
                var (g, source) = Fixture("copy", count); int seat = g.Actor;
                long submissions = g.Submissions, wrappers = g.WrapperSteps;
                int logStart = g.Engine.Log.Count;
                g.Rebuild();
                Check(g.Decision?.Context != "soi.mode" && g.Engine.State.Players[seat].Gems == 3 * count,
                    "Every copied mode takes free extra gem");
                Check(g.Submissions == submissions + count && g.WrapperSteps == wrappers,
                    "Atomic engine submissions counted; no unnecessary policy decisions");
                Check(!source.BanishAtCleanup && g.Engine.State.Players[seat].PlayZone.Contains(source),
                    "Copied Reactor never banishes copier");
                var events = Enumerable.Range(logStart, g.Engine.Log.Count - logStart).Select(i => g.Engine.Log[i]).ToArray();
                Check(events.OfType<ShardsModeChosenEvent>().Count(e =>
                    e.PlayerIndex == seat && e.Label == "Gain 3 gems, then banish this card") == count &&
                    events.OfType<ShardsGemsChangedEvent>().Count(e =>
                    e.PlayerIndex == seat && e.Delta == 3) == count,
                    "All actual mode and gem events remain available to statistics callbacks");
            }
            foreach (string mutation in new[] { "title", "context", "kind", "option", "disabled", "bounds" })
            {
                var (g, _) = Fixture("copy");
                switch (mutation)
                {
                    case "title": g.Decision.Title = "Different effect"; break;
                    case "context": g.Decision.Context = "soi.volos"; break;
                    case "kind": g.Decision.Kind = DecisionKind.ChooseCards; break;
                    case "option": g.Decision.Options[1].Label = "Different cost"; break;
                    case "disabled": g.Decision.Options[1].Disabled = true; break;
                    case "bounds": g.Decision.Min = 0; break;
                }
                long before = g.Submissions; g.Rebuild();
                Check(g.Submissions == before && g.Decision != null, "Exact menu guard: " + mutation);
            }
            Program.Print(new { passed = true, checks = _checks,
                fixtures = "physical/copy/hidden/null/already-marked/menu-guard/128-consecutive-copies",
                automation = "only public non-Reactor source with exact Reactor mode contract" });
        }
    }
}
