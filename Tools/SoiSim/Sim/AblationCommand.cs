using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>`ablation` — measures whether the PLAY axis or the ACQUISITION axis carries
    /// more strength in SoI, by degrading one at a time and reading the Elo cost.
    ///
    /// Why this runs before any planner work. The deck-builder literature is split: Dominion
    /// says buying dominates (Provincial's play model is deliberately simple; the Dominion
    /// DQN learns buys only and still matches it), while Slay the Spire is the inverse. SoI
    /// has two properties Dominion lacks — mastery thresholds that resolve mid-sequence, and
    /// multiplicative burst — so neither answer transfers. One experiment settles it for
    /// THIS game, and the answer decides where the planner spends its beam: wide over
    /// purchase baskets, or wide over play orderings.
    ///
    /// Method. All four arms use the SAME two-stage architecture (<see cref="PhaseHybridBot"/>),
    /// so the only difference between them is which tuned vector governs which phase — the
    /// architecture itself cancels out. Each arm plays mirrored PAIRS against one fixed
    /// opponent; pair scoring cancels the seed, the character matchup and the ~59% first-player
    /// advantage, and measures roughly 1.4x tighter than pooling the same games.
    ///
    /// STRONG = V5 (sep-CMA-ES tuned with Duel ON). WEAK = V1 (the hand-set starting vector).
    /// V1 is a real policy rather than a random one, so the measured gap is "well-tuned vs
    /// untuned" on that axis — the honest question — not "plays vs flails".</summary>
    public static class AblationCommand
    {
        private sealed class Arm
        {
            public string Name;
            public double[] PlayWeights;
            public double[] BuyWeights;
            public readonly List<double> PairScores = new();
        }

        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 2000);
            ulong seedBase = cli.GetULong("--seed-base", 770000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            // The "weak" vector is configurable so the result can be checked for robustness
            // against the SIZE of the strong/weak gap: V1 is the hand-set start (a huge gap),
            // V4 is the previous champion (V5 beats it only 69.9%). If the axis ranking
            // survives both, it is a property of the game and not of one degradation.
            string weakName = cli.GetStr("--weak", "V1");
            cli.RejectUnknown();

            int pairs = Math.Max(1, games / 2);

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);

            var strong = W.Pad(ShardsEvalWeights.V5);
            var weak = BotFactory.ResolveWeights(weakName)
                       ?? throw new CliError($"--weak must name a historical vector, got '{weakName}'");

            var arms = new[]
            {
                new Arm { Name = "strong play / strong buy", PlayWeights = strong, BuyWeights = strong },
                new Arm { Name = "strong play / WEAK buy  ", PlayWeights = strong, BuyWeights = weak },
                new Arm { Name = "WEAK play  / strong buy ", PlayWeights = weak, BuyWeights = strong },
                new Arm { Name = "WEAK play  / WEAK buy   ", PlayWeights = weak, BuyWeights = weak }
            };

            // Models are expensive to build (each walks every def at 7 mastery buckets), so
            // build one per distinct vector and share them across threads (read-only).
            var strongModel = new ShardsValueModel(strong);
            var weakModel = new ShardsValueModel(weak);
            ShardsValueModel ModelFor(double[] w) => ReferenceEquals(w, strong) ? strongModel : weakModel;

            Console.WriteLine($"ablation: 4 arms x {pairs} pairs vs a fixed bench:greedy-v5 opponent");
            Console.WriteLine($"  STRONG = V5 (tuned, Duel ON) · WEAK = {weakName}");
            var sw = Stopwatch.StartNew();

            foreach (var arm in arms)
            {
                var scores = new double[pairs];
                Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = threads }, p =>
                {
                    ulong seed = seedBase + (ulong)p;
                    var rng = new DeterministicRng(seed * 31 + 7, 91);
                    int a = rng.Next(chars.Count);
                    int b = rng.Next(chars.Count - 1);
                    if (b >= a) b++;
                    double s0 = PlayOne(seed, true, chars[a], chars[b], arm, ModelFor);
                    double s1 = PlayOne(seed, false, chars[a], chars[b], arm, ModelFor);
                    // A pair where either orientation stalled scores 0.5 rather than being
                    // dropped, so every arm keeps an identical seed set (common random
                    // numbers across arms is the whole point).
                    scores[p] = (s0 < 0 || s1 < 0) ? 0.5 : 0.5 * (s0 + s1);
                });
                arm.PairScores.AddRange(scores);
            }

            Console.WriteLine($"  wall: {sw.Elapsed.TotalSeconds:F1}s " +
                              $"({4.0 * pairs * 2 / sw.Elapsed.TotalSeconds:F0} games/s on {threads} threads)");
            Console.WriteLine();
            Console.WriteLine($"  {"arm",-26} | {"score",16} | {"Elo",8}");
            Console.WriteLine($"  {new string('-', 26)}-+-{new string('-', 16)}-+-{new string('-', 8)}");

            var elo = new double[arms.Length];
            for (int i = 0; i < arms.Length; i++)
            {
                var (mean, lo, hi, _) = Stats.MeanCi(arms[i].PairScores);
                elo[i] = Stats.ScoreToElo(mean);
                Console.WriteLine($"  {arms[i].Name,-26} | {mean * 100,5:F1} % [{lo * 100,4:F1}-{hi * 100,4:F1}] | " +
                                  $"{elo[i],+8:F0}");
            }

            // Attribution: how much Elo is lost by untuning ONE axis while the other stays
            // tuned. The larger drop is the axis that carries the strength.
            double buyCost = elo[0] - elo[1];
            double playCost = elo[0] - elo[2];
            double bothCost = elo[0] - elo[3];

            Console.WriteLine();
            Console.WriteLine($"  Elo lost by untuning the BUY  axis alone: {buyCost,7:F0}");
            Console.WriteLine($"  Elo lost by untuning the PLAY axis alone: {playCost,7:F0}");
            Console.WriteLine($"  Elo lost by untuning BOTH:                {bothCost,7:F0}");
            if (Math.Abs(buyCost) + Math.Abs(playCost) > 1e-9)
            {
                double share = 100.0 * Math.Abs(buyCost) / (Math.Abs(buyCost) + Math.Abs(playCost));
                Console.WriteLine($"  → the BUY axis carries {share:F0} % of the attributable strength");
            }
            double additivity = bothCost - (buyCost + playCost);
            Console.WriteLine($"  interaction (both - sum of parts):        {additivity,7:F0} " +
                              "(near 0 = the axes are separable)");

            CampaignStatus.Complete("ablation",
                $"ablation {pairs} pairs: buy {buyCost:F0} Elo · play {playCost:F0} Elo · both {bothCost:F0} Elo");
            return 0;
        }

        /// <summary>Plays one orientation. Returns the arm's score, or -1 if the game stalled.</summary>
        private static double PlayOne(ulong seed, bool armFirst, string c0, string c1, Arm arm,
            Func<double[], ShardsValueModel> modelFor)
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "S0", CharacterId = c0 },
                new() { Name = "S1", CharacterId = c1 }
            };
            var adapter = new ShardsEngineAdapter(
                ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));

            int armSeat = armFirst ? 0 : 1;
            var seats = new IBotAgent[2];
            seats[armSeat] = new PhaseHybridBot(adapter.Inner,
                modelFor(arm.PlayWeights), modelFor(arm.BuyWeights));
            seats[1 - armSeat] = ShardsBotRanks.Create("bench:greedy-v5",
                seed * 100 + (ulong)(1 - armSeat), adapter.Inner);

            int guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
            {
                var pending = adapter.PendingInput;
                if (pending == null) break;
                var action = seats[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    break;
            }
            if (!adapter.GameOver) return -1;
            return adapter.WinnerIndex < 0 ? 0.5 : adapter.WinnerIndex == armSeat ? 1 : 0;
        }
    }
}
