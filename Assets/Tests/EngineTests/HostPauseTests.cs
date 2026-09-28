using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Net;

namespace Pascension.Engine.Tests
{
    /// <summary>Disconnect pauses human input and timers; replacement sessions resync.</summary>
    [TestFixture]
    public class HostPauseTests
    {
        /// <summary>Minimal seat that records what the host pushes to it.</summary>
        private sealed class RecordingSeat : IHostSeat
        {
            public int PlayerIndex { get; }
            public readonly List<List<GameEvent>> Events = new();
            public readonly List<SnapshotBase> Snapshots = new();
            public PendingSnap LastPending;

            public RecordingSeat(int playerIndex) => PlayerIndex = playerIndex;
            public void DeliverEvents(List<GameEvent> filteredEvents) => Events.Add(filteredEvents);
            public void DeliverSnapshot(SnapshotBase snapshot) => Snapshots.Add(snapshot);
            public void OnInputRequested(PendingSnap pending) => LastPending = pending;
        }

        private static GameEngine EngineOf(GameHost host) => ((PascensionEngineAdapter)host.Engine).Inner;

        private static GameHost HostWithHumanSeat(out RecordingSeat human, out RecordingSeat other, int timerSeconds = 0)
        {
            var config = TestGames.StandardConfig(players: 2, seed: 77);
            config.Rules.ResponseTimerSeconds = timerSeconds;
            var adapter = new PascensionEngineAdapter(config);
            var host = new GameHost(adapter, 2, config.Rules.ResponseTimerSeconds);
            human = new RecordingSeat(0);
            other = new RecordingSeat(1);
            host.AttachSeat(human, isHuman: true);
            host.AttachSeat(other, isHuman: true);
            host.Start();
            return host;
        }

        [Test]
        public void Paused_Submit_IsRejected_AndEngineUnchanged()
        {
            var host = HostWithHumanSeat(out var human, out _);
            Assert.IsNotNull(human.LastPending, "P0 starts with the pending input");
            var hashBefore = EngineOf(host).State.ComputeHash();

            host.SetPaused(true);
            string rejection = null;
            host.SeatActionRejected += (player, error) => rejection = $"P{player}: {error}";
            host.Submit(0, new PassPriorityAction());

            Assert.AreEqual("P0: The game is paused", rejection);
            Assert.AreEqual(hashBefore, EngineOf(host).State.ComputeHash(), "Engine untouched");
        }

        [Test]
        public void Paused_Tick_FreezesResponseTimer()
        {
            var host = HostWithHumanSeat(out _, out _, timerSeconds: 5);
            var pendingBefore = EngineOf(host).PendingInput;

            host.SetPaused(true);
            host.Tick(60f); // way past the 5s timer — must not auto-play the default
            Assert.AreSame(pendingBefore, EngineOf(host).PendingInput, "Nothing advanced while paused");

            host.SetPaused(false);
            host.Tick(6f); // now the timer fires and auto-passes P0
            Assert.AreNotSame(pendingBefore, EngineOf(host).PendingInput, "Timer resumed after unpause");
        }

        [Test]
        public void ReplaceSeat_DeliversFreshSnapshot_NoStaleEventFlood()
        {
            var host = HostWithHumanSeat(out var human, out _);
            // Generate some history first.
            host.Submit(0, host.Engine.DefaultActionFor(host.Engine.PendingInput));
            for (int i = 0; i < 20; i++)
                host.Submit(host.Engine.PendingInput.PlayerIndex, host.Engine.DefaultActionFor(host.Engine.PendingInput));

            var replacement = new RecordingSeat(0);
            host.ReplaceSeat(0, replacement, isHuman: true);

            Assert.AreEqual(1, replacement.Snapshots.Count, "Fresh snapshot delivered on replacement");
            Assert.AreEqual(0, replacement.Events.Count, "No stale event flood");

            // The next broadcast must not replay old events either.
            if (EngineOf(host).PendingInput != null)
                host.Submit(host.Engine.PendingInput.PlayerIndex, host.Engine.DefaultActionFor(host.Engine.PendingInput));
            foreach (var batch in replacement.Events)
                foreach (var e in batch)
                    Assert.GreaterOrEqual(e.Seq, replacement.Snapshots[0].EventSeq,
                        "Only events newer than the replacement snapshot arrive");
        }
    }
}
