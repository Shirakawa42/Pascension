using NUnit.Framework;
using Pascension.Bots;
using Pascension.Engine.Core;
using Pascension.Engine.Events;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    [Category("Simulation")]
    public class SimulationTests
    {
        [Test]
        public void Soak_RandomBots_NeverBreakRulesOrLeakHiddenInfo()
        {
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var engine = new GameEngine(TestGames.StandardConfig(players: 4, seed: seed));
                int cardsAtStart = engine.State.TotalCards();
                var agents = new ISyncAgent[]
                {
                    new RandomBot(seed * 10 + 1), new RandomBot(seed * 10 + 2),
                    new RandomBot(seed * 10 + 3), new RandomBot(seed * 10 + 4)
                };

                GameDriver.Run(engine, agents, maxRounds: 30);

                // Card conservation across every zone (Mind Steal copies may add cards, never remove).
                Assert.GreaterOrEqual(engine.State.TotalCards(), cardsAtStart, $"Seed {seed}: cards vanished");

                // Masking: a filtered log for P1 must never reveal what P0 drew.
                foreach (var e in engine.Log.FilterFor(1))
                    if (e is CardDrawnEvent drawn && drawn.PlayerIndex == 0)
                        Assert.IsNull(drawn.DefId, $"Seed {seed}: P0's draw leaked to P1");
            }
        }

        [Test]
        public void Balance_HeuristicBots_GameProgressesTowardTheBoss()
        {
            int finished = 0;
            int totalRounds = 0;
            for (ulong seed = 1; seed <= 10; seed++)
            {
                var engine = new GameEngine(TestGames.StandardConfig(players: 4, seed: seed));
                var agents = new ISyncAgent[]
                {
                    new HeuristicBot(seed * 10 + 1), new HeuristicBot(seed * 10 + 2),
                    new HeuristicBot(seed * 10 + 3), new HeuristicBot(seed * 10 + 4)
                };

                GameDriver.Run(engine, agents, maxRounds: 60);

                int maxLevel = 0, maxPos = 0;
                foreach (var p in engine.State.Players)
                {
                    if (p.Level > maxLevel) maxLevel = p.Level;
                    if (p.Position > maxPos) maxPos = p.Position;
                }
                TestContext.WriteLine(
                    $"Seed {seed}: over={engine.State.GameOver} winner={engine.State.WinnerIndex} " +
                    $"rounds={engine.State.Round} maxLevel={maxLevel} maxPos={maxPos}");

                if (engine.State.GameOver) finished++;
                totalRounds += engine.State.Round;

                Assert.GreaterOrEqual(maxLevel, 3, $"Seed {seed}: bots never leveled");
                Assert.GreaterOrEqual(maxPos, 20, $"Seed {seed}: bots never raced");
            }
            TestContext.WriteLine($"Finished: {finished}/10, avg rounds {totalRounds / 10}");
            Assert.GreaterOrEqual(finished, 5, "At least half the games should reach a boss kill within 60 rounds");
        }
    }
}
