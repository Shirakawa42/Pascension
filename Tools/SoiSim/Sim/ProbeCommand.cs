using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Head-to-head strength + timing probe: mirrored games between two bot
    /// kinds. The timing numbers size the Phase-B stats runs.
    ///
    /// The unit of work is a PAIR: the same seed and character matchup played twice with
    /// the seats swapped. Scoring per pair rather than per game cancels the seed, the
    /// matchup and the first-player advantage (P0 wins 56.5% of SoI games) — all pure
    /// nuisance variance — so the reported interval is 2-4x tighter than pooling the
    /// games as if they were independent. The unpaired Wilson figure is still printed
    /// for continuity with older campaign-log lines, but the PAIRED number is the one
    /// to quote and the one every gate is written against.</summary>
    public static class ProbeCommand
    {
        public static int Run(Cli cli)
        {
            string kindA = cli.GetStr("--a", "strong");
            int budgetA = cli.GetInt("--budget-a", cli.GetInt("--budget", 200));
            string kindB = cli.GetStr("--b", "greedy");
            int budgetB = cli.GetInt("--budget-b", 0);
            int games = cli.GetInt("--games", 100);
            ulong seedBase = cli.GetULong("--seed-base", 9000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            double epsilon = cli.Has("--epsilon") ? cli.GetDouble("--epsilon", -1) : -1;
            int truncateA = cli.GetInt("--truncate-a", -1);
            int truncateB = cli.GetInt("--truncate-b", -1);
            double wallclock = cli.GetDouble("--wallclock", 0);
            // Per-side budgets for cross-budget rank duels (e.g. a 1.25s candidate
            // vs the 1.0s rank below, both AS SHIPPED). Fall back to the shared value.
            double wallclockA = cli.GetDouble("--wallclock-a", wallclock);
            double wallclockB = cli.GetDouble("--wallclock-b", wallclock);
            int workersA = cli.GetInt("--workers-a", 1);
            int workersB = cli.GetInt("--workers-b", 1);
            // Tuned weight vector per side: current | duel-blind | V1…Vn. Drives the
            // promotion gate (Vn vs Vn-1) and the Duel-awareness ablation.
            string weightsA = cli.GetStr("--weights-a", null);
            string weightsB = cli.GetStr("--weights-b", null);
            // RESEARCH: let a side CHEAT (skip determinization, plan against the real
            // hidden state). Oracle-vs-honest bounds what hidden information costs, and
            // therefore what any belief/encoder work could ever be worth.
            bool oracleA = cli.Has("--oracle-a");
            bool oracleB = cli.Has("--oracle-b");
            // Early-stop budget fraction applied to BOTH search seats: -1 default,
            // 0 off, >0 the fraction (1.0 exact, lower = more aggressive).
            double earlyStop = cli.Has("--earlystop") ? cli.GetDouble("--earlystop", -1) : -1;
            // NOTE: `--record`/`--record-per-game` (v1-schema position capture) were removed
            // 2026-07-27 with the encoder. Position capture returns in Phase 3 against the
            // clock-feature schema, with dedup, opening-ply skipping and quiet-position
            // filtering — none of which the old writer did.
            // Machine-readable result (score/decisive/games) for cross-worker aggregation.
            string resultPath = cli.GetStr("--result", null);
            // Sequential testing: stop as soon as the result is decided either way.
            bool sprt = cli.Has("--sprt");
            double elo0 = cli.GetDouble("--elo0", 0);
            double elo1 = cli.GetDouble("--elo1", 15);
            double alpha = cli.GetDouble("--alpha", 0.05);
            double beta = cli.GetDouble("--beta", 0.05);
            // A probe below the resolution floor must not write a conclusion anyone
            // will later cite. n=120 gives a +/-9pt half-width; several false findings
            // in the campaign log came from exactly that.
            bool allowSmall = cli.Has("--allow-small");
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var factoryA = new BotFactory(kindA, budgetA)
                { Epsilon = epsilon, WallClockSeconds = wallclockA, RootWorkers = workersA, EarlyStopFraction = earlyStop, Weights = BotFactory.ResolveWeights(weightsA), PerfectInformation = oracleA };
            var factoryB = new BotFactory(kindB, budgetB)
                { Epsilon = epsilon, WallClockSeconds = wallclockB, RootWorkers = workersB, EarlyStopFraction = earlyStop, Weights = BotFactory.ResolveWeights(weightsB), PerfectInformation = oracleB };
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);

            // One work item = one mirrored PAIR (both seat orientations of one seed).
            var pairs = new List<(ulong Seed, string C0, string C1)>();
            var rng = new DeterministicRng(seedBase, 55);
            for (int p = 0; p < games / 2; p++)
            {
                ulong s = seedBase + (ulong)p;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                pairs.Add((s, chars[a], chars[b]));
            }

            double totalScore = 0;
            long totalSubmits = 0, aDecisionMs = 0, aDecisions = 0;
            int done = 0, failures = 0;
            var pairScores = new List<double>(pairs.Count);
            string sprtVerdict = null;
            int sprtStopPairs = 0;
            double sprtStopLlr = 0;
            object sync = new();
            var sw = Stopwatch.StartNew();
            var (sprtLo, sprtHi) = Stats.SprtBounds(alpha, beta);
            using var cts = new CancellationTokenSource();

            // Plays one orientation of a pair. Returns -1 for a game that never terminated.
            double PlayOne(ulong seed, bool aFirst, string c0, string c1,
                           ref long submits, ref long aMs, ref long aCount)
            {
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = c0 },
                    new() { Name = "S1", CharacterId = c1 }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
                int aSeat = aFirst ? 0 : 1;
                var seats = new IBotAgent[2];
                seats[aSeat] = factoryA.Create(seed, aSeat, adapter.Inner);
                seats[1 - aSeat] = factoryB.Create(seed, 1 - aSeat, adapter.Inner);

                int guard = 0;
                var decisionSw = new Stopwatch();
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    bool isA = pending.PlayerIndex == aSeat;
                    if (isA) decisionSw.Restart();
                    var actingBot = seats[pending.PlayerIndex];
                    var action = actingBot.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
                    if (isA)
                    {
                        aMs += decisionSw.ElapsedMilliseconds;
                        aCount++;
                    }
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
                submits += guard;

                return !adapter.GameOver ? -1
                    : adapter.WinnerIndex < 0 ? 0.5
                    : adapter.WinnerIndex == aSeat ? 1 : 0;
            }

            try
            {
                Parallel.ForEach(pairs,
                    new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cts.Token },
                    item =>
                    {
                        long submits = 0, aMs = 0, aCount = 0;
                        double s0 = PlayOne(item.Seed, true, item.C0, item.C1, ref submits, ref aMs, ref aCount);
                        double s1 = PlayOne(item.Seed, false, item.C1, item.C0, ref submits, ref aMs, ref aCount);

                        lock (sync)
                        {
                            if (s0 < 0) failures++; else totalScore += s0;
                            if (s1 < 0) failures++; else totalScore += s1;
                            done += 2;
                            totalSubmits += submits;
                            aDecisionMs += aMs;
                            aDecisions += aCount;
                            // A pair only scores if BOTH orientations terminated — a half
                            // pair carries the seat bias we are pairing to remove.
                            if (s0 >= 0 && s1 >= 0) pairScores.Add((s0 + s1) / 2.0);

                            if (sprt && sprtVerdict == null && pairScores.Count >= 20 && pairScores.Count % 10 == 0)
                            {
                                double llr = Stats.GsprtLlr(pairScores, elo0, elo1);
                                if (llr <= sprtLo || llr >= sprtHi)
                                {
                                    sprtVerdict = llr >= sprtHi
                                        ? $"H1 accepted (>= {elo1:F0} Elo)"
                                        : $"H0 accepted (<= {elo0:F0} Elo)";
                                    sprtStopPairs = pairScores.Count;
                                    sprtStopLlr = llr;
                                    Console.WriteLine($"  SPRT stopped at {sprtStopPairs} pairs: " +
                                                      $"{sprtVerdict}, LLR {llr:F2}");
                                    cts.Cancel();
                                }
                            }

                            if (done % 20 == 0)
                            {
                                double perSec = done / sw.Elapsed.TotalSeconds;
                                var (m, _, _, _) = Stats.MeanCi(pairScores);
                                CampaignStatus.Update($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor}",
                                    $"**Progress**: {done} / {pairs.Count * 2} games · {perSec:F2} games/s · " +
                                    $"ETA {(pairs.Count * 2 - done) / Math.Max(0.01, perSec) / 60:F0} min\n\n" +
                                    $"**A paired win rate**: {m:P1} over {pairScores.Count} pairs");
                                if (done % 100 == 0)
                                    Console.WriteLine($"  {done}/{pairs.Count * 2}  {perSec:F2} games/s");
                            }
                        }
                    });
            }
            catch (OperationCanceledException) { /* SPRT stopped early — results so far stand. */ }
            sw.Stop();

            int decisive = done - failures;
            double rawWr = decisive == 0 ? 0 : totalScore / decisive;
            var (lo, hi) = Stats.Wilson((int)Math.Round(totalScore), Math.Max(1, decisive));
            var (paired, pLo, pHi, pSd) = Stats.MeanCi(pairScores);
            double llrFinal = sprt ? Stats.GsprtLlr(pairScores, elo0, elo1) : 0;

            Console.WriteLine($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor}, {done} games" +
                              (failures > 0 ? $" ({failures} FAILURES)" : ""));
            double eloDiff = Stats.ScoreToElo(paired);
            Console.WriteLine($"  A win rate (PAIRED): {paired:P1} [{pLo:P1}–{pHi:P1}] " +
                              $"over {pairScores.Count} pairs (sd {pSd:F3}, {(eloDiff >= 0 ? "+" : "")}{eloDiff:F0} Elo)");
            Console.WriteLine($"  A win rate (unpaired): {rawWr:P1} [{lo:P1}–{hi:P1}]");
            if (sprt && sprtVerdict != null)
                // The decision statistic is the one at the crossing. Pairs already in
                // flight when we cancelled still land in the point estimate above (more
                // data is better), but they can drag the LLR back inside the bounds —
                // that does not un-decide a completed sequential test.
                Console.WriteLine($"  SPRT: {sprtVerdict} at {sprtStopPairs} pairs " +
                                  $"(LLR {sprtStopLlr:F2}, bounds [{sprtLo:F2}, {sprtHi:F2}]" +
                                  (pairScores.Count > sprtStopPairs
                                      ? $"; {pairScores.Count - sprtStopPairs} in-flight pairs also counted above)"
                                      : ")"));
            else if (sprt)
                Console.WriteLine($"  SPRT: inconclusive — LLR {llrFinal:F2} still inside " +
                                  $"[{sprtLo:F2}, {sprtHi:F2}] after {pairScores.Count} pairs");

            // The campaign log is the project's memory; a conclusion below the resolution
            // floor does more harm there than no entry at all. A completed SPRT is exempt:
            // reaching a bound IS the statistically valid stopping rule, which is the whole
            // reason to run one.
            const int MinPairsToPublish = 200;
            bool publishable = pairScores.Count >= MinPairsToPublish || sprtVerdict != null || allowSmall;
            if (publishable)
                CampaignStatus.Log($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor} → " +
                                   $"{paired:P1} [{pLo:P1}–{pHi:P1}] paired over {pairScores.Count} pairs" +
                                   (sprtVerdict != null ? $" · SPRT {sprtVerdict}" : "") +
                                   (pairScores.Count < MinPairsToPublish && sprtVerdict == null
                                       ? " · UNDERPOWERED (--allow-small)" : ""));
            else
                Console.WriteLine($"  NOT logged: {pairScores.Count} pairs is below the {MinPairsToPublish}-pair " +
                                  $"floor for a citable result. Re-run with more --games, or pass --allow-small.");

            Console.WriteLine($"  wall: {sw.Elapsed.TotalSeconds:F1}s ({done / sw.Elapsed.TotalSeconds:F2} games/s on {threads} threads)");
            Console.WriteLine($"  A think time: {(aDecisions == 0 ? 0 : aDecisionMs / (double)aDecisions):F0} ms/decision ({aDecisions} decisions)");
            if (resultPath != null)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var full = Path.GetFullPath(resultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                // paired_sum / paired_sumsq let a fan-out aggregate the PAIRED estimate
                // exactly across pods (mean and variance are both additive over pairs).
                // Without them the orchestrator can only pool games unpaired, throwing
                // away the whole reason for pairing.
                double pairedSum = 0, pairedSumSq = 0;
                foreach (double s in pairScores) { pairedSum += s; pairedSumSq += s * s; }
                File.WriteAllText(full,
                    $"{{\"score\":{totalScore.ToString(inv)},\"decisive\":{decisive}," +
                    $"\"games\":{done},\"failures\":{failures},\"wr\":{rawWr.ToString(inv)}," +
                    $"\"pairs\":{pairScores.Count},\"paired_wr\":{paired.ToString(inv)}," +
                    $"\"paired_lo\":{pLo.ToString(inv)},\"paired_hi\":{pHi.ToString(inv)}," +
                    $"\"paired_sum\":{pairedSum.ToString(inv)},\"paired_sumsq\":{pairedSumSq.ToString(inv)}}}");
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
