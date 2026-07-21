using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// A seat for slow synchronous bots (the SoI ISMCTS ranks, ~1-2.5s per decision):
    /// PRIORITY searches run on a worker task so the frame never blocks; DECISION
    /// answers (instant plan-cursor/model paths) are computed synchronously on the
    /// main thread — the worker therefore never reads the live engine outside Fork.
    /// Answers queue thread-safely via GameHost.SubmitAsync and apply on the next
    /// host Tick (which re-checks the pending player, covering replacement races).
    ///
    /// THE SEAT CAN NEVER HANG THE GAME: any exception from the agent is caught,
    /// reported via SeatFaulted, and the engine's safe default action is submitted
    /// instead. (The original fire-and-forget version silently swallowed faults and
    /// froze the game forever — GameHost has no bot response timeout.)
    ///
    /// Threading contract: while this seat holds the pending input, nothing advances
    /// the engine (single-pending-input discipline; SoI has no response timers), so
    /// the worker's engine READS (Fork's DeepCopy) never race a mutation.
    /// </summary>
    public sealed class SearchBotSeat : IHostSeat
    {
        public int PlayerIndex { get; }

        /// <summary>True while a priority search runs on the worker — drives the
        /// in-game "thinking" indicator. Written on worker/main, read on main.</summary>
        public bool IsThinking => Volatile.Read(ref _thinking) != 0;

        /// <summary>Telemetry of the last completed search (main-thread reads).</summary>
        public int LastIterations { get; private set; }
        public long LastMs { get; private set; }

        /// <summary>(playerIndex, error) whenever the agent faulted and the safe
        /// default was submitted instead. Raised on the worker thread — subscribers
        /// must marshal to Unity themselves if needed (Debug.LogError is safe).</summary>
        public event Action<int, string> SeatFaulted;

        /// <summary>(playerIndex, iterations, elapsedMs) after each priority search.</summary>
        public event Action<int, int, long> SearchCompleted;

        private readonly IBotAgent _agent;
        private readonly GameHost _host;
        private int _requestToken;
        private int _thinking;

        public SearchBotSeat(int playerIndex, IBotAgent agent, GameHost host)
        {
            PlayerIndex = playerIndex;
            _agent = agent;
            _host = host;
        }

        public void DeliverEvents(List<GameEvent> filteredEvents) { }
        public void DeliverSnapshot(SnapshotBase snapshot) { }

        public void OnInputRequested(PendingSnap pending)
        {
            int token = Interlocked.Increment(ref _requestToken);

            // Decisions are instant (plan cursor / value model) — answer on the main
            // thread so the worker never touches the live engine outside Fork.
            if (pending.Kind == PendingInputKind.Decision)
            {
                _host.SubmitAsync(PlayerIndex, SafeChoose(pending));
                return;
            }

            Volatile.Write(ref _thinking, 1);
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var action = SafeChoose(pending);
                sw.Stop();
                LastIterations = (_agent as Shards.Bots.ShardsSearchBot)?.LastIterations ?? -1;
                LastMs = sw.ElapsedMilliseconds;
                Volatile.Write(ref _thinking, 0);
                SearchCompleted?.Invoke(PlayerIndex, LastIterations, LastMs);
                if (token == Volatile.Read(ref _requestToken))
                    _host.SubmitAsync(PlayerIndex, action);
            });
        }

        private Pascension.Engine.Actions.PlayerAction SafeChoose(PendingSnap pending)
        {
            try
            {
                return _agent.Choose(pending, null)
                       ?? _host.Engine.DefaultActionFor(pending);
            }
            catch (Exception ex)
            {
                SeatFaulted?.Invoke(PlayerIndex, ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                try
                {
                    return _host.Engine.DefaultActionFor(pending);
                }
                catch (Exception inner)
                {
                    SeatFaulted?.Invoke(PlayerIndex, "DefaultActionFor also failed: " + inner.Message);
                    return new Pascension.Engine.Actions.ConcedeAction { PlayerIndex = PlayerIndex };
                }
            }
        }
    }
}
