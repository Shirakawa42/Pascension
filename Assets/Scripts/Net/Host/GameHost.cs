using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>A player seat as the host sees it: local human, remote human, or bot.</summary>
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
    /// seats; filters every event batch per player; drives bot pacing and the human
    /// response timer via Tick().
    /// </summary>
    public sealed class GameHost
    {
        public readonly IEngineAdapter Engine;
        private readonly IHostSeat[] _seats;
        private readonly int[] _lastSeq;
        private readonly ConcurrentQueue<(int player, PlayerAction action)> _asyncSubmissions = new();

        private float _pendingElapsed;
        private readonly float _responseTimeout;
        /// <summary>Timer only runs for seats flagged as human (bots have their own pacing).</summary>
        private readonly bool[] _isHuman;

        public event Action<int, string> SeatActionRejected;

        /// <summary>
        /// While paused (a remote human disconnected mid-game) the world freezes:
        /// submits are rejected, async submissions queue up, bots hold, timers stop.
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

        /// <summary>Submit an action on behalf of a seat (UI, remote client, or bot callback).</summary>
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

        /// <summary>Thread-safe submission for async agents (Ollama) — applied on the next Tick.</summary>
        public void SubmitAsync(int playerIndex, PlayerAction action) =>
            _asyncSubmissions.Enqueue((playerIndex, action));

        /// <summary>Drive timers and async submissions. Call every frame (or in a loop headless).</summary>
        public void Tick(float deltaSeconds)
        {
            if (Paused) return; // async submissions stay queued and apply on the first unpaused tick

            while (_asyncSubmissions.TryDequeue(out var queued))
            {
                var pendingNow = Engine.PendingInput;
                if (pendingNow != null && pendingNow.PlayerIndex == queued.player)
                    Submit(queued.player, queued.action);
            }

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
        /// Swap a seat's occupant mid-game (host kicks a disconnected player → bot).
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

    /// <summary>A synchronous bot occupying a seat, paced by a think-delay.
    /// Game-agnostic — the smarts live in the <see cref="IBotAgent"/>.</summary>
    public sealed class BotSeat : IHostSeat
    {
        public int PlayerIndex { get; }
        private readonly IBotAgent _agent;
        private readonly float _thinkDelay;
        private GameHost _host;
        private PendingSnap _pending;
        private SnapshotBase _view;
        private float _wait;

        public BotSeat(int playerIndex, IBotAgent agent, float thinkDelaySeconds = 0.6f)
        {
            PlayerIndex = playerIndex;
            _agent = agent;
            _thinkDelay = thinkDelaySeconds;
        }

        public void Bind(GameHost host) => _host = host;

        public void DeliverEvents(List<GameEvent> filteredEvents) { }
        public void DeliverSnapshot(SnapshotBase snapshot) => _view = snapshot;

        public void OnInputRequested(PendingSnap pending)
        {
            _pending = pending;
            _wait = 0;
        }

        /// <summary>Call every frame; submits after the think-delay. Zero delay submits immediately.</summary>
        public void Tick(float deltaSeconds)
        {
            if (_pending == null || _host == null) return;
            if (_host.Paused) return; // never clear _pending into a gated Submit — hold until unpaused
            var enginePending = _host.Engine.PendingInput;
            if (enginePending == null || enginePending.PlayerIndex != PlayerIndex)
            {
                _pending = null;
                return;
            }

            _wait += deltaSeconds;
            if (_wait < _thinkDelay) return;

            var pending = _pending;
            _pending = null;
            var action = _agent.Choose(pending, _view) ?? _host.Engine.DefaultActionFor(pending);
            _host.Submit(PlayerIndex, action);
        }
    }
}
