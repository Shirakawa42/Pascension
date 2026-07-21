using System;
using System.Collections.Generic;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Fast self-check: a handful of heuristic games must produce valid,
    /// deterministic records. Exit 1 on any invariant failure. The NUnit twin of this
    /// runs in CI via the Engine.Verify source link.</summary>
    public static class SmokeCommand
    {
        public static int Run(Cli cli)
        {
            cli.RejectUnknown();
            var problems = Check();
            foreach (string p in problems)
                Console.Error.WriteLine("SMOKE FAIL: " + p);
            Console.WriteLine(problems.Count == 0 ? "smoke: OK" : $"smoke: {problems.Count} failures");
            return problems.Count == 0 ? 0 : 1;
        }

        /// <summary>Shared with the NUnit smoke tests — returns a list of violations.</summary>
        public static List<string> Check()
        {
            var problems = new List<string>();
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            var factory = new BotFactory("heuristic", 0);
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);

            for (ulong seed = 1; seed <= 10; seed++)
            {
                var spec = new GameSpec
                {
                    Seed = seed,
                    Characters = new[]
                    {
                        chars[(int)(seed % (ulong)chars.Count)],
                        chars[(int)((seed + 1) % (ulong)chars.Count)]
                    }
                };
                var r = SimGameRunner.RunOne(spec, factory);
                Validate(r, problems);
            }

            // Determinism: the same spec twice must serialize identically (minus wall time).
            var probe = new GameSpec { Seed = 5, Characters = new[] { chars[0], chars[1] } };
            var a = SimGameRunner.RunOne(probe, factory);
            var b = SimGameRunner.RunOne(probe, factory);
            a.WallMs = 0;
            b.WallMs = 0;
            if (SimJson.Line(a) != SimJson.Line(b))
                problems.Add("same seed+config produced different records");

            // Scheduler seat balance must be exact.
            var work = SimScheduler.BuildWorkList(SimScheduler.Matchups(SimConfig.AllDlc), 10, 1);
            if (work.Count != 150)
                problems.Add($"work list should be 15 matchups × 10 = 150, got {work.Count}");
            var seatCounts = new Dictionary<string, int>();
            foreach (var w in work)
                if (w.Characters[0] != w.Characters[1])
                {
                    string key = w.Characters[0] + ">" + w.Characters[1];
                    seatCounts[key] = seatCounts.TryGetValue(key, out int v) ? v + 1 : 1;
                }
            foreach (var kv in seatCounts)
                if (kv.Value != 5)
                    problems.Add($"seating {kv.Key} appears {kv.Value} times, expected exactly 5");

            return problems;
        }

        private static void Validate(GameRecord r, List<string> problems)
        {
            string id = $"seed {r.Seed}";
            if (r.Termination is not ("kill" or "overwhelm" or "tie"))
                problems.Add($"{id}: termination {r.Termination} ({r.Error})");
            if (r.Winner is < -1 or > 1)
                problems.Add($"{id}: winner {r.Winner} out of range");
            if (r.Termination == "tie" != (r.Winner == -1))
                problems.Add($"{id}: tie/winner mismatch");
            if (r.Rounds < 1)
                problems.Add($"{id}: rounds {r.Rounds}");
            if (r.Players.Count != 2)
                problems.Add($"{id}: expected 2 player records");

            foreach (var p in r.Players)
            {
                foreach (var kv in p.Buys)
                {
                    if (!r.RowOffers.TryGetValue(kv.Key, out int offered) || kv.Value > offered)
                        problems.Add($"{id}: {p.Character} bought {kv.Key} ×{kv.Value} but offers show {(r.RowOffers.TryGetValue(kv.Key, out int o) ? o : 0)}");
                    if (!p.BuyRounds.ContainsKey(kv.Key))
                        problems.Add($"{id}: buy {kv.Key} has no first-buy round");
                }
                if (p.MasteryByRound.Count == 0)
                    problems.Add($"{id}: {p.Character} has an empty mastery curve");
                if (p.FinalMastery < 0 || p.FinalMastery > 30)
                    problems.Add($"{id}: {p.Character} final mastery {p.FinalMastery}");
            }

            if (r.Winner >= 0)
            {
                var loser = r.Players[1 - r.Winner];
                if (r.Termination is "kill" or "overwhelm" && loser.FinalHealth > 0)
                    problems.Add($"{id}: decisive game but loser has {loser.FinalHealth} health");
            }
        }
    }
}
