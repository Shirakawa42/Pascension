using NUnit.Framework;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;
using Pascension.Net;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class SerializationTests
    {
        [Test]
        public void Actions_RoundTripThroughJson()
        {
            var attack = new AssignDamageAction
            {
                PlayerIndex = 2,
                Target = TargetRef.Monster(1, 3),
                Amount = 4
            };
            string json = EngineJson.SerializeAction(attack);
            var back = (AssignDamageAction)EngineJson.DeserializeAction(json);
            Assert.AreEqual(2, back.PlayerIndex);
            Assert.AreEqual(TargetRef.Monster(1, 3), back.Target);
            Assert.AreEqual(4, back.Amount);

            var decision = new SubmitDecisionAction
            {
                PlayerIndex = 1,
                Answer = new Decisions.DecisionAnswer { DecisionId = 7, ChosenOptionIds = { 2, 0 } }
            };
            var back2 = (SubmitDecisionAction)EngineJson.DeserializeAction(EngineJson.SerializeAction(decision));
            Assert.AreEqual(7, back2.Answer.DecisionId);
            CollectionAssert.AreEqual(new[] { 2, 0 }, back2.Answer.ChosenOptionIds);
        }

        [Test]
        public void Events_RoundTrip_AndSnapshotMasksOpponentHands()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 5));

            var events = engine.Log.FilterFor(1);
            string json = EngineJson.SerializeEvents(events);
            var back = EngineJson.DeserializeEvents(json);
            Assert.AreEqual(events.Count, back.Count);

            var snapForP1 = SnapshotBuilder.Build(engine, 1);
            Assert.AreEqual(0, snapForP1.Players[0].Hand.Count, "Opponent hand cards not included");
            Assert.AreEqual(5, snapForP1.Players[0].HandCount, "But the count is public");
            Assert.AreEqual(5, snapForP1.Players[1].Hand.Count, "Own hand fully visible");
            Assert.IsNotNull(snapForP1.Pending, "Someone holds priority");

            // Snapshot itself serializes.
            var snapJson = EngineJson.Serialize(snapForP1);
            var snapBack = EngineJson.Deserialize<ClientSnapshot>(snapJson);
            Assert.AreEqual(snapForP1.Players.Count, snapBack.Players.Count);
            Assert.AreEqual(snapForP1.PileCounts[0], snapBack.PileCounts[0]);
        }

        [Test]
        public void GameHost_RoutesHumanInputsAndSnapshots()
        {
            var adapter = new PascensionEngineAdapter(TestGames.StandardConfig(players: 3, seed: 11));
            var host = new GameHost(adapter, 3, 0f);
            var sessions = new LocalSession[3];
            var snapshots = new int[3];
            var requests = new int[3];
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i] = new LocalSession(host, i);
                sessions[i].SnapshotReceived += _ => snapshots[index]++;
                sessions[i].InputRequested += _ => requests[index]++;
                host.AttachSeat(sessions[i], isHuman: true);
            }
            host.Start();
            for (int step = 0; step < 300 && !adapter.GameOver; step++)
            {
                var pending = adapter.PendingInput;
                Assert.IsNotNull(pending);
                sessions[pending.PlayerIndex].SubmitAction(adapter.DefaultActionFor(pending));
            }
            for (int i = 0; i < sessions.Length; i++)
            {
                Assert.Greater(snapshots[i], 10, "Every human receives snapshots");
                Assert.Greater(requests[i], 1, "Input reaches every human seat");
            }
        }
    }
}
