using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Bots;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Net;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Pause/kick policy for online play: while a remote human is disconnected the
    /// whole match freezes (submits rejected, bots hold, timers stop); the host may
    /// replace the missing player's seat with a bot, which resumes play — including
    /// when the engine was waiting on that very seat.
    /// </summary>
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

        private static GameHost HostWithHumanSeat(out RecordingSeat human, out BotSeat bot, int timerSeconds = 0)
        {
            var config = TestGames.StandardConfig(players: 2, seed: 77);
            config.Rules.ResponseTimerSeconds = timerSeconds;
            var adapter = new PascensionEngineAdapter(config);
            var host = new GameHost(adapter, 2, config.Rules.ResponseTimerSeconds);
            human = new RecordingSeat(0);
            bot = new BotSeat(1, new SyncAgentBot(new HeuristicBot(202), adapter.Inner), thinkDelaySeconds: 0f);
            bot.Bind(host);
            host.AttachSeat(human, isHuman: true);
            host.AttachSeat(bot, isHuman: false);
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
        public void Paused_AsyncSubmissions_ApplyAfterUnpause()
        {
            var host = HostWithHumanSeat(out var human, out _);
            host.SetPaused(true);
            host.SubmitAsync(0, new PassPriorityAction());
            host.Tick(1f);
            Assert.AreEqual(0, EngineOf(host).PendingInput.PlayerIndex, "Still waiting on P0 while paused");

            host.SetPaused(false);
            host.Tick(0.01f);
            // The queued pass applied: priority moved on (P1's bot may even have acted).
            Assert.IsTrue(EngineOf(host).PendingInput == null || EngineOf(host).PendingInput.PlayerIndex != 0 ||
                          EngineOf(host).State.Phase != Phase.Main || human.Snapshots.Count > 1,
                "The queued action was applied on the first unpaused tick");
        }

        [Test]
        public void BotSeat_DoesNotAct_WhilePaused()
        {
            // Give the BOT the pending input: P0 (human seat) passes its own priority first.
            var host = HostWithHumanSeat(out var human, out var bot);
            host.Submit(0, host.Engine.DefaultActionFor(host.Engine.PendingInput));
            // Drive until the engine waits on P1 (the bot seat).
            for (int i = 0; i < 100 && EngineOf(host).PendingInput?.PlayerIndex != 1; i++)
            {
                if (EngineOf(host).PendingInput?.PlayerIndex == 0)
                    host.Submit(0, host.Engine.DefaultActionFor(host.Engine.PendingInput));
                bot.Tick(0.1f);
            }
            Assert.AreEqual(1, EngineOf(host).PendingInput?.PlayerIndex, "Engine waits on the bot seat");

            host.SetPaused(true);
            for (int i = 0; i < 50; i++) bot.Tick(0.1f);
            Assert.AreEqual(1, EngineOf(host).PendingInput?.PlayerIndex, "Bot held while paused");

            host.SetPaused(false);
            for (int i = 0; i < 50 && EngineOf(host).PendingInput?.PlayerIndex == 1; i++) bot.Tick(0.1f);
            Assert.AreNotEqual(1, EngineOf(host).PendingInput?.PlayerIndex, "Bot acted after unpause");
        }

        [Test]
        public void ReplaceSeat_MidPendingInput_BotTakesOver_AndGameCompletes()
        {
            var host = HostWithHumanSeat(out var human, out var bot1);
            Assert.AreEqual(0, EngineOf(host).PendingInput.PlayerIndex, "Engine waits on the 'disconnected' human");

            // Pause (disconnect), replace the human seat with a bot (kick), unpause.
            host.SetPaused(true);
            var replacement = new BotSeat(0,
                new SyncAgentBot(new HeuristicBot(303), EngineOf(host)), thinkDelaySeconds: 0f);
            replacement.Bind(host);
            host.ReplaceSeat(0, replacement, isHuman: false);
            host.SetPaused(false);

            // The pending input was re-routed to the replacement bot; the game must now
            // run to completion (or the round cap) with zero human involvement.
            for (int i = 0; i < 200000 && !EngineOf(host).State.GameOver && EngineOf(host).State.Round <= 40; i++)
            {
                host.Tick(0.1f);
                replacement.Tick(0.1f);
                bot1.Tick(0.1f);
            }
            Assert.IsTrue(EngineOf(host).State.GameOver || EngineOf(host).State.Round > 40,
                "No stall after seat replacement — the bot answered the pending input");
        }

        [Test]
        public void ReplaceSeat_DeliversFreshSnapshot_NoStaleEventFlood()
        {
            var host = HostWithHumanSeat(out var human, out var bot);
            // Generate some history first.
            host.Submit(0, host.Engine.DefaultActionFor(host.Engine.PendingInput));
            for (int i = 0; i < 20; i++) { host.Tick(0.1f); bot.Tick(0.1f); }

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
