using NUnit.Framework;
using Pascension.Bots;
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
        public void GameHost_RunsASoloGameWithSeats()
        {
            var config = TestGames.StandardConfig(players: 3, seed: 11);
            var host = new GameHost(config);

            // Seat 0 is a "human" driven through LocalSession; seats 1-2 are instant bots.
            var session = new LocalSession(host, 0);
            host.AttachSeat(session, isHuman: true);
            var bot1 = new BotSeat(1, new HeuristicBot(101), thinkDelaySeconds: 0f);
            var bot2 = new BotSeat(2, new HeuristicBot(102), thinkDelaySeconds: 0f);
            bot1.Bind(host);
            bot2.Bind(host);
            host.AttachSeat(bot1, isHuman: false);
            host.AttachSeat(bot2, isHuman: false);

            int snapshots = 0, inputRequests = 0;
            var humanAgent = new HeuristicBot(100);
            session.SnapshotReceived += _ => snapshots++;
            session.InputRequested += pending =>
            {
                inputRequests++;
                // Answer immediately, exactly as the UI would.
                if (pending.Kind == PendingInputKind.Decision)
                    session.SubmitAction(new SubmitDecisionAction
                    {
                        PlayerIndex = 0,
                        Answer = humanAgent.ChooseDecision(host.Engine, pending.Decision)
                    });
                else
                    session.SubmitAction(humanAgent.ChooseAction(host.Engine, new PendingInput
                    {
                        Kind = pending.Kind,
                        PlayerIndex = 0,
                        LegalActions = pending.LegalActions
                    }));
            };

            host.Start();
            // Bots act on ticks; the human acts via the event handler above.
            for (int i = 0; i < 200000 && !host.Engine.State.GameOver && host.Engine.State.Round <= 40; i++)
            {
                host.Tick(0.1f);
                bot1.Tick(0.1f);
                bot2.Tick(0.1f);
            }

            Assert.Greater(snapshots, 10, "Session received snapshots");
            Assert.Greater(inputRequests, 10, "Session received input requests");
            Assert.IsTrue(host.Engine.State.GameOver || host.Engine.State.Round > 40,
                "Game progressed to completion or the cap");
        }
    }
}
