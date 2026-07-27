using NUnit.Framework;

namespace SoiSim.Tests
{
    /// <summary>Guards the sibling-ranking harness (`soisim rank`) — the instrument that
    /// decides whether ANY evaluator-steered reranking can beat the tuned policy, built
    /// after the first planner was built on an unmeasured assumption and scored 12.7%.
    ///
    /// Two properties matter enough to pin:
    ///  · it runs and produces sibling pairs at all (the sampling, forking, tail and
    ///    rollout plumbing all hold together);
    ///  · it is DETERMINISTIC — the fit command's ±1-point run-to-run wobble came from
    ///    scheduler-ordered collection, and this harness uses the same per-game-slot
    ///    discipline. If two identical runs ever disagree, that discipline broke.</summary>
    [TestFixture]
    public sealed class SoiSimRankTests
    {
        private static RankOptions Tiny() => new()
        {
            Games = 6,
            Rollouts = 8,
            MaxPointsPerGame = 2,
            Threads = 4,
            SeedBase = 880000
        };

        [Test]
        public void RankHarness_ProducesPairs_AndIsDeterministic()
        {
            var first = RankCommand.RunCore(Tiny(), _ => { });
            Assert.Greater(first.Points, 0, "no decision points sampled");
            Assert.Greater(first.Pairs, 0, "no sibling pairs formed");
            for (int s = 0; s < 4; s++)
                for (int b = 0; b < RankCommand.Buckets; b++)
                {
                    double agreement = first.Agreement(s, b);
                    Assert.That(agreement, Is.InRange(0.0, 1.0));
                }

            var second = RankCommand.RunCore(Tiny(), _ => { });
            Assert.AreEqual(first.Points, second.Points, "point count not reproducible");
            Assert.AreEqual(first.Pairs, second.Pairs, "pair count not reproducible");
            Assert.AreEqual(first.MeanRawRegret, second.MeanRawRegret,
                "regret not bit-reproducible — scheduler order is leaking into the harness");
            for (int s = 0; s < 4; s++)
                Assert.AreEqual(first.Headroom[s], second.Headroom[s],
                    $"headroom[{s}] not bit-reproducible");
        }

        [Test]
        public void RankHarness_BasketMode_ProducesPairs_AndIsDeterministic()
        {
            var options = Tiny();
            options.Baskets = true;
            var first = RankCommand.RunCore(options, _ => { });
            Assert.Greater(first.Points, 0, "no basket points sampled");
            Assert.Greater(first.Pairs, 0, "no basket sibling pairs formed");
            var second = RankCommand.RunCore(options, _ => { });
            Assert.AreEqual(first.Pairs, second.Pairs, "basket pair count not reproducible");
            Assert.AreEqual(first.MeanRawRegret, second.MeanRawRegret,
                "basket regret not bit-reproducible");
        }

        [Test]
        public void RankHarness_SingleThread_MatchesParallel()
        {
            // The stronger claim: thread COUNT cannot change the numbers either. This is
            // what proves no shared mutable state hides in the fork/rollout path.
            var parallel = RankCommand.RunCore(Tiny(), _ => { });
            var serial = Tiny();
            serial.Threads = 1;
            var single = RankCommand.RunCore(serial, _ => { });
            Assert.AreEqual(parallel.Points, single.Points);
            Assert.AreEqual(parallel.Pairs, single.Pairs);
            Assert.AreEqual(parallel.MeanRawRegret, single.MeanRawRegret,
                "threads=1 vs threads=4 disagree — shared mutable state in the rollout path");
        }
    }
}
