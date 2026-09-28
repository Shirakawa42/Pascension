using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    public sealed class ShardsCardIndexTests
    {
        [TestCase(0)]
        [TestCase(1)]
        public void StolenFuturesNewDestinyRemainsFindableThroughAPreviouslyBuiltIndex(int seat)
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(9051000000000000008,
                new List<PlayerSpec> { new() { Name = "P0", CharacterId = "decima" }, new() { Name = "P1", CharacterId = "rez" } }, ShardsDlc.Duel));
            while (adapter.PendingInput?.Kind == PendingInputKind.Decision)
                Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted);
            var engine = adapter.Inner;
            if (seat == 1) Assert.IsTrue(engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 }).Accepted);
            var state = engine.State;
            var player = state.Players[seat];
            player.Mastery = 10;
            var stolen = state.DestinyDeck.Concat(state.DestinyRow).Single(c => c.DefId == "stolen_futures");
            state.DestinyDeck.Remove(stolen);
            state.DestinyRow.Remove(stolen);
            stolen.Owner = seat;
            stolen.Zone = ShardsZone.SetAside;
            player.Destinies.Add(stolen);
            state.InvalidateCardIndex();
            var newlyRevealed = state.DestinyDeck.Last();
            int creationCounter = state.NextInstanceId;
            Assert.AreSame(stolen, state.FindCard(stolen.InstanceId)); // Prime before revealing.

            Assert.IsTrue(engine.Submit(new ShardsExhaustAction { PlayerIndex = seat, CardInstanceId = stolen.InstanceId }).Accepted);
            Assert.AreEqual("soi.destiny", engine.PendingInput.Decision.Context);
            Assert.IsTrue(engine.PendingInput.Decision.Options.Any(o => o.CardInstanceId == newlyRevealed.InstanceId));
            var request = engine.PendingInput.Decision;
            Assert.IsTrue(engine.Submit(new SubmitDecisionAction { PlayerIndex = seat, Answer = new DecisionAnswer
            {
                DecisionId = request.Id,
                ChosenOptionIds = new List<int> { newlyRevealed.InstanceId }
            } }).Accepted);
            Assert.IsTrue(player.Destinies.Contains(newlyRevealed));
            Assert.AreEqual(creationCounter, state.NextInstanceId, "Moving an existing destiny does not create a new card.");
            Assert.AreSame(newlyRevealed, state.FindCard(newlyRevealed.InstanceId), "Newly public destiny must not disappear from live tactical lookup.");
        }
    }
}
