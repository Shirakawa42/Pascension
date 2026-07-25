using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>The measuring instrument's own calibration.
    ///
    /// Every strength claim in this project is a probe result, so the probe needs a
    /// known-answer test. An agent against an identical copy of itself is worth exactly
    /// 50%: if the harness reports otherwise, every downstream number is suspect. The
    /// campaign log contains real examples of self-vs-self readings of 60.0% and 39.0%
    /// that were never recognised as noise, and several conclusions were drawn on top
    /// of them.
    ///
    /// Uses `greedy` (instant, deterministic) so the whole fixture costs about a second.</summary>
    [TestFixture]
    public sealed class SoiSimProbeCalibrationTests
    {
        private const int Pairs = 400;

        // The null run is deterministic, so compute it once and share it. Note this
        // fixture deliberately does NOT call ShardsCardDatabase.Clear(): BotFactory's
        // greedy model is a process-wide Lazy keyed on ShardsCardDef object identity,
        // so clearing the database after it is built strands every cached entry.
        private static List<double> _pairScores, _gameScores;

        [OneTimeSetUp]
        public void RunTheNullOnce()
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var rng = new DeterministicRng(4242, 55);

            _pairScores = new List<double>(Pairs);
            _gameScores = new List<double>(Pairs * 2);
            for (int p = 0; p < Pairs; p++)
            {
                ulong seed = 4242 + (ulong)p;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                double s0 = PlayOne(seed, true, chars[a], chars[b], "greedy");
                double s1 = PlayOne(seed, false, chars[b], chars[a], "greedy");
                if (s0 < 0 || s1 < 0) continue;
                _gameScores.Add(s0);
                _gameScores.Add(s1);
                _pairScores.Add((s0 + s1) / 2.0);
            }
        }

        private static double PlayOne(ulong seed, bool aFirst, string c0, string c1, string kind)
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "S0", CharacterId = c0 },
                new() { Name = "S1", CharacterId = c1 }
            };
            var adapter = new ShardsEngineAdapter(
                ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
            int aSeat = aFirst ? 0 : 1;
            var factory = new BotFactory(kind, 0);
            var seats = new IBotAgent[2];
            seats[aSeat] = factory.Create(seed, aSeat, adapter.Inner);
            seats[1 - aSeat] = factory.Create(seed, 1 - aSeat, adapter.Inner);

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
            return !adapter.GameOver ? -1
                : adapter.WinnerIndex < 0 ? 0.5
                : adapter.WinnerIndex == aSeat ? 1 : 0;
        }

        [Test]
        public void NullCalibration_SelfVersusSelf_StraddlesFiftyPercent()
        {
            var pairScores = _pairScores;
            Assert.GreaterOrEqual(pairScores.Count, Pairs - 5,
                "too many games failed to terminate for this to be a calibration");

            var (mean, lo, hi, _) = Stats.MeanCi(pairScores);
            Assert.Less(lo, 0.5, $"paired lower bound {lo:P1} excludes 50% (mean {mean:P1}) — " +
                                 "an agent cannot beat an identical copy of itself; the probe " +
                                 "harness or the mirroring is biased");
            Assert.Greater(hi, 0.5, $"paired upper bound {hi:P1} excludes 50% (mean {mean:P1}) — " +
                                    "same problem, opposite sign");
        }

        [Test]
        public void Pairing_IsTighterThanPoolingGamesIndependently()
        {
            // The entire justification for pair-scoring: mirroring cancels the seed, the
            // character matchup and the first-player advantage (P0 wins ~56.5% of SoI
            // games). If this stops holding, the gate sample sizes in the plan are wrong.
            var (_, pLo, pHi, _) = Stats.MeanCi(_pairScores);
            var (_, gLo, gHi, _) = Stats.MeanCi(_gameScores);
            double pairedHalf = (pHi - pLo) / 2, unpairedHalf = (gHi - gLo) / 2;

            Assert.Less(pairedHalf, unpairedHalf,
                $"paired half-width {pairedHalf:P2} should beat unpaired {unpairedHalf:P2}");
            // Both intervals cover the same number of GAMES, so any tightening is pure
            // variance reduction. Anything under ~1.15x means the mirroring is broken.
            Assert.Greater(unpairedHalf / pairedHalf, 1.15,
                $"pairing only bought {unpairedHalf / pairedHalf:F2}x — mirroring may be broken");
        }

        [Test]
        public void Sprt_DoesNotFireOnATrueNull()
        {
            // A false H1 here would mean the gate promotes nets that are not better.
            double llr = Stats.GsprtLlr(_pairScores, 0, 15);
            var (_, upper) = Stats.SprtBounds();
            Assert.Less(llr, upper,
                $"SPRT accepted H1 (>= 15 Elo) on a self-vs-self null: LLR {llr:F2}");
        }
    }
}
