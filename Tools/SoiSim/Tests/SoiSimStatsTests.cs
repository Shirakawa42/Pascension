using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace SoiSim.Tests
{
    /// <summary>Pins the statistics core against precomputed values — the defensibility
    /// of the balance report rests on this math.</summary>
    [TestFixture]
    public sealed class SoiSimStatsTests
    {
        [Test]
        public void Wilson_MatchesKnownValues()
        {
            // 50/100 → classic Wilson bounds ~[0.4038, 0.5962].
            var (lo, hi) = Stats.Wilson(50, 100);
            Assert.AreEqual(0.4038, lo, 0.001);
            Assert.AreEqual(0.5962, hi, 0.001);

            // 0/10 stays a valid interval anchored at 0.
            (lo, hi) = Stats.Wilson(0, 10);
            Assert.AreEqual(0.0, lo, 1e-9);
            Assert.AreEqual(0.2775, hi, 0.001);

            (lo, hi) = Stats.Wilson(0, 0);
            Assert.AreEqual(0, lo, 1e-9);
            Assert.AreEqual(1, hi, 1e-9);
        }

        [Test]
        public void NormalCdf_And_TwoSidedP()
        {
            Assert.AreEqual(0.5, Stats.NormalCdf(0), 1e-6);
            Assert.AreEqual(0.9750, Stats.NormalCdf(1.959964), 0.0005);
            Assert.AreEqual(0.05, Stats.TwoSidedP(1.959964), 0.001);
            Assert.AreEqual(1.0, Stats.TwoSidedP(0), 1e-6);
        }

        [Test]
        public void PooledDelta_SingleStratum_MatchesDirectComputation()
        {
            // 60/100 vs 40/100 → delta 0.20; Anscombe-adjusted SE ≈ 0.0692.
            var (delta, se, z, p, n) = Stats.PooledDelta(new[] { (60, 100, 40, 100) });
            Assert.AreEqual(0.20, delta, 1e-9);
            Assert.AreEqual(200, n);
            Assert.AreEqual(0.0692, se, 0.001);
            Assert.AreEqual(2.89, z, 0.02);
            Assert.Less(p, 0.005);
        }

        [Test]
        public void PooledDelta_WeighsLargerStrataMore()
        {
            // Stratum A: +20 pts on n=2000; stratum B: −20 pts on n=20.
            var (delta, _, _, _, _) = Stats.PooledDelta(new[]
            {
                (600, 1000, 400, 1000),
                (4, 10, 6, 10)
            });
            Assert.Greater(delta, 0.15, "the big stratum must dominate");
        }

        [Test]
        public void BenjaminiHochberg_FlagsTheClassicExample()
        {
            // Standard worked example at FDR 25%.
            var p = new List<double> { 0.01, 0.013, 0.014, 0.19, 0.35, 0.5, 0.63, 0.67, 0.75, 0.81 };
            var flags = Stats.BenjaminiHochberg(p, 0.25);
            Assert.IsTrue(flags[0] && flags[1] && flags[2]);
            for (int i = 3; i < flags.Length; i++)
                Assert.IsFalse(flags[i], $"index {i}");
        }

        [Test]
        public void Logistic_RecoversAKnownSeparation()
        {
            // One feature, deterministic-ish separation: x>0 wins 80%, x<0 wins 20%.
            var rng = new Random(7);
            var x = new List<double[]>();
            var y = new List<int>();
            for (int i = 0; i < 2000; i++)
            {
                double v = rng.NextDouble() * 2 - 1;
                bool win = rng.NextDouble() < (v > 0 ? 0.8 : 0.2);
                x.Add(new[] { v });
                y.Add(win ? 1 : 0);
            }
            var fit = Stats.Logistic(x.ToArray(), y.ToArray());
            Assert.IsNotNull(fit);
            var (beta, se) = fit.Value;
            Assert.Greater(beta[1], 1.0, "positive slope expected");
            Assert.Greater(beta[1] / se[1], 5, "strongly significant");
        }

        [Test]
        public void MeanCi_MatchesDirectComputation()
        {
            // Four pair scores: mean 0.5, sample sd 0.408, SE 0.204, half-width 0.400.
            var (mean, lo, hi, sd) = Stats.MeanCi(new[] { 0.0, 0.5, 0.5, 1.0 });
            Assert.AreEqual(0.5, mean, 1e-9);
            Assert.AreEqual(0.4082, sd, 0.001);
            Assert.AreEqual(0.1, lo, 0.001);
            Assert.AreEqual(0.9, hi, 0.001);

            // A degenerate sample must not report a fake-tight interval.
            var (m1, l1, h1, _) = Stats.MeanCi(new[] { 0.5 });
            Assert.AreEqual(0.5, m1, 1e-9);
            Assert.AreEqual(0, l1, 1e-9);
            Assert.AreEqual(1, h1, 1e-9);
        }

        [Test]
        public void EloAndScore_RoundTrip()
        {
            Assert.AreEqual(0.5, Stats.EloToScore(0), 1e-12);
            Assert.AreEqual(0.0, Stats.ScoreToElo(0.5), 1e-9);
            // ScoreToElo must never emit negative zero — it formats as "-0" and reads
            // as a loss in reports.
            Assert.IsFalse(double.IsNegative(Stats.ScoreToElo(0.5)));
            foreach (double elo in new[] { -300.0, -15.0, 15.0, 300.0 })
                Assert.AreEqual(elo, Stats.ScoreToElo(Stats.EloToScore(elo)), 1e-6);
        }

        [Test]
        public void Gsprt_AcceptsH1OnAStrongEdge_AndH0OnANullEdge()
        {
            var (lower, upper) = Stats.SprtBounds();
            Assert.Less(lower, 0);
            Assert.Greater(upper, 0);

            // A decisive edge drives the LLR above the upper bound.
            var strong = new List<double>();
            for (int i = 0; i < 200; i++) strong.Add(i % 10 == 0 ? 0.5 : 1.0);
            Assert.Greater(Stats.GsprtLlr(strong, 0, 15), upper);

            // A true null drives it below the lower bound (H0: <= 0 Elo).
            var nul = new List<double>();
            for (int i = 0; i < 3000; i++) nul.Add(i % 2 == 0 ? 0.25 : 0.75);
            Assert.Less(Stats.GsprtLlr(nul, 0, 15), lower);

            // Degenerate zero-variance input must not produce a verdict.
            var flat = new List<double>();
            for (int i = 0; i < 100; i++) flat.Add(0.5);
            Assert.AreEqual(0, Stats.GsprtLlr(flat, 0, 15), 1e-12);
        }

        [Test]
        public void Percentile_Interpolates()
        {
            var sorted = new List<int> { 10, 20, 30, 40 };
            Assert.AreEqual(10, Stats.Percentile(sorted, 0));
            Assert.AreEqual(40, Stats.Percentile(sorted, 1));
            Assert.AreEqual(25, Stats.Percentile(sorted, 0.5), 1e-9);
        }
    }
}
