using NUnit.Framework;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class SnapshotFieldTests
    {
        [Test]
        public void EffectiveHp_ReflectsContinuousModifiers()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("protective_barrier", 10) };
            config.BasicPile.Add("goblin", 8);
            var engine = new GameEngine(config);

            var snap0 = SnapshotBuilder.Build(engine, 0);
            Assert.AreEqual(2, snap0.MarketRows[0][0].EffectiveHp, "Base goblin HP");

            engine.PlayFromHand(0, "protective_barrier");
            engine.AnswerWithTarget(TargetRef.Monster((int)CardTier.Basic, 0));
            engine.PassUntilStackEmpty();

            var snap1 = SnapshotBuilder.Build(engine, 0);
            Assert.AreEqual(5, snap1.MarketRows[0][0].EffectiveHp, "Barrier: 2 + 3 until end of turn");
            Assert.AreEqual(20, snap1.Boss.EffectiveHp, "Boss reports rules HP");
        }

        [Test]
        public void EffectiveCost_IsPerViewer()
        {
            var config = TestGames.BareConfig();
            config.Players[0].HeroId = "cornelius"; // Haggle: first buy each turn costs 1 less
            config.BasicPile.Add("run", 8);         // printed cost 2
            var engine = new GameEngine(config);

            var forCornelius = SnapshotBuilder.Build(engine, 0);
            var forOther = SnapshotBuilder.Build(engine, 1);
            Assert.AreEqual(1, forCornelius.MarketRows[0][0].EffectiveCost, "Haggle discount applies to the viewer");
            Assert.AreEqual(2, forOther.MarketRows[0][0].EffectiveCost, "Other players see the printed cost");
        }

        [Test]
        public void DeckContents_PopulatedForAllPlayers_SortedByName_OrderHidden()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 9));
            var snapForP1 = SnapshotBuilder.Build(engine, 1);

            var opponentDeck = snapForP1.Players[0].Deck;
            Assert.AreEqual(snapForP1.Players[0].DeckCount, opponentDeck.Count,
                "Opponent deck contents are public (full-transparency decision)");
            for (int i = 1; i < opponentDeck.Count; i++)
            {
                string prev = CardDatabase.Get(opponentDeck[i - 1].DefId).Name;
                string cur = CardDatabase.Get(opponentDeck[i].DefId).Name;
                Assert.LessOrEqual(string.CompareOrdinal(prev, cur), 0, "Alphabetical order (hides draw order)");
            }
            // Hands remain masked.
            Assert.AreEqual(0, snapForP1.Players[0].Hand.Count);
            Assert.AreEqual(5, snapForP1.Players[0].HandCount);
        }
    }
}
