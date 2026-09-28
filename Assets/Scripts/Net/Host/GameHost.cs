using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>A player seat as the host sees it: local or remote human.</summary>
    public interface IHostSeat
    {
        int PlayerIndex { get; }
        void DeliverEvents(List<GameEvent> filteredEvents);
        void DeliverSnapshot(SnapshotBase snapshot);
        /// <summary>The engine is waiting on this seat.</summary>
        void OnInputRequested(PendingSnap pending);
    }

    /// <summary>
    /// Host-side orchestrator (plain C# — runs in solo play and as the NGO host).
    /// Game-agnostic: owns an <see cref="IEngineAdapter"/>; routes pending input to
    /// seats; filters every event batch per player; drives the human
    /// response timer via Tick().
    /// </summary>
    public sealed class GameHost
    {
        public readonly IEngineAdapter Engine;
        private readonly IHostSeat[] _seats;
        private readonly int[] _lastSeq;

        private float _pendingElapsed;
        private readonly float _responseTimeout;
        /// <summary>Timer runs for attached human seats.</summary>
        private readonly bool[] _isHuman;

        public event Action<int, string> SeatActionRejected;

        /// <summary>
        /// While paused (a remote human disconnected mid-game) the world freezes:
        /// submits are rejected and timers stop.
        /// The orchestrator (HostMatchStarter) pauses/unpauses around disconnects.
        /// </summary>
        public bool Paused { get; private set; }

        public GameHost(IEngineAdapter engine, int playerCount, float responseTimeoutSeconds)
        {
            Engine = engine;
            _seats = new IHostSeat[playerCount];
            _lastSeq = new int[playerCount];
            _isHuman = new bool[playerCount];
            _responseTimeout = responseTimeoutSeconds;
        }

        public void AttachSeat(IHostSeat seat, bool isHuman)
        {
            _seats[seat.PlayerIndex] = seat;
            _isHuman[seat.PlayerIndex] = isHuman;
        }

        /// <summary>Call after all seats are attached: initial snapshots + first input routing.</summary>
        public void Start()
        {
            Broadcast();
            RouteInput();
        }

        public void SetPaused(bool paused) => Paused = paused;

        /// <summary>Submit an action on behalf of a seat (UI or remote client).</summary>
        public void Submit(int playerIndex, PlayerAction action)
        {
            if (Paused)
            {
                SeatActionRejected?.Invoke(playerIndex, "The game is paused");
                return;
            }
            action.PlayerIndex = playerIndex;
            var result = Engine.Submit(action);
            if (!result.Accepted)
            {
                SeatActionRejected?.Invoke(playerIndex, result.Error);
                return;
            }
            _pendingElapsed = 0;
            Broadcast();
            RouteInput();
        }

        /// <summary>Drive response timers. Call every frame (or in a loop headless).</summary>
        public void Tick(float deltaSeconds)
        {
            if (Paused) return;

            var current = Engine.PendingInput;
            if (current == null || Engine.GameOver) return;
            if (!_isHuman[current.PlayerIndex]) return;

            _pendingElapsed += deltaSeconds;
            if (_responseTimeout > 0 && _pendingElapsed >= _responseTimeout)
            {
                _pendingElapsed = 0;
                Submit(current.PlayerIndex, Engine.DefaultActionFor(current));
            }
        }

        /// <summary>Full masked snapshot for one seat (join/reconnect).</summary>
        public SnapshotBase SnapshotFor(int playerIndex) => Engine.BuildSnapshot(playerIndex);

        /// <summary>
        /// Replace a human session after reconnecting.
        /// The new seat gets a fresh snapshot (no stale event flood) and, if the engine
        /// is currently waiting on this seat, the pending input is re-issued to it.
        /// </summary>
        public void ReplaceSeat(int playerIndex, IHostSeat seat, bool isHuman)
        {
            _seats[playerIndex] = seat;
            _isHuman[playerIndex] = isHuman;
            _lastSeq[playerIndex] = Engine.EventCount;
            seat.DeliverSnapshot(Engine.BuildSnapshot(playerIndex));

            var pending = Engine.PendingInput;
            if (pending != null && pending.PlayerIndex == playerIndex)
                RouteInput();
        }

        private void Broadcast()
        {
            for (int i = 0; i < _seats.Length; i++)
            {
                var seat = _seats[i];
                if (seat == null) continue;
                var events = Engine.FilterEventsFor(i, _lastSeq[i]);
                _lastSeq[i] = Engine.EventCount;
                if (events.Count > 0)
                    seat.DeliverEvents(events);
                seat.DeliverSnapshot(Engine.BuildSnapshot(i));
            }
        }

        private void RouteInput()
        {
            var pending = Engine.PendingInput;
            if (pending == null) return;
            var seat = _seats[pending.PlayerIndex];
            if (seat == null) return;
            _pendingElapsed = 0;
            seat.OnInputRequested(pending);
        }
    }
}
