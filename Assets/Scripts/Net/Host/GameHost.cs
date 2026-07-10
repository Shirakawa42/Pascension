using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Pascension.Bots;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>A player seat as the host sees it: local human, remote human, or bot.</summary>
    public interface IHostSeat
    {
        int PlayerIndex { get; }
        void DeliverEvents(List<GameEvent> filteredEvents);
        void DeliverSnapshot(ClientSnapshot snapshot);
        /// <summary>The engine is waiting on this seat.</summary>
        void OnInputRequested(PendingSnap pending);
    }

    /// <summary>
    /// Host-side orchestrator (plain C# — runs in solo play and as the NGO host).
    /// Owns the engine; routes pending input to seats; filters every event batch
    /// per player; drives bot pacing and the human response timer via Tick().
    /// </summary>
    public sealed class GameHost
    {
        public readonly GameEngine Engine;
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

        public GameHost(GameConfig config)
        {
            Engine = new GameEngine(config);
            _seats = new IHostSeat[config.Players.Count];
            _lastSeq = new int[config.Players.Count];
            _isHuman = new bool[config.Players.Count];
            _responseTimeout = config.Rules.ResponseTimerSeconds;
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
                var pending = Engine.PendingInput;
                if (pending != null && pending.PlayerIndex == queued.player)
                    Submit(queued.player, queued.action);
            }

            var current = Engine.PendingInput;
            if (current == null || Engine.State.GameOver) return;
            if (!_isHuman[current.PlayerIndex]) return;

            _pendingElapsed += deltaSeconds;
            if (_responseTimeout > 0 && _pendingElapsed >= _responseTimeout)
            {
                _pendingElapsed = 0;
                Submit(current.PlayerIndex, DefaultActionFor(current));
            }
        }

        /// <summary>The action taken when a player times out or disconnects mid-input.</summary>
        public static PlayerAction DefaultActionFor(PendingInput pending)
        {
            if (pending.Kind == PendingInputKind.Priority)
                return new PassPriorityAction { PlayerIndex = pending.PlayerIndex };

            var req = pending.Decision;
            var answer = new DecisionAnswer { DecisionId = req.Id };
            answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
            for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(req.Options[i].Id))
                    answer.ChosenOptionIds.Add(req.Options[i].Id);
            while (answer.ChosenOptionIds.Count > req.Max)
                answer.ChosenOptionIds.RemoveAt(answer.ChosenOptionIds.Count - 1);
            return new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer };
        }

        /// <summary>Full masked snapshot for one seat (join/reconnect).</summary>
        public ClientSnapshot SnapshotFor(int playerIndex) => SnapshotBuilder.Build(Engine, playerIndex);

        /// <summary>
        /// Swap a seat's occupant mid-game (host kicks a disconnected player → bot).
        /// The new seat gets a fresh snapshot (no stale event flood) and, if the engine
        /// is currently waiting on this seat, the pending input is re-issued to it.
        /// </summary>
        public void ReplaceSeat(int playerIndex, IHostSeat seat, bool isHuman)
        {
            _seats[playerIndex] = seat;
            _isHuman[playerIndex] = isHuman;
            _lastSeq[playerIndex] = Engine.Log.Count;
            seat.DeliverSnapshot(SnapshotFor(playerIndex));

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
                var events = Engine.Log.FilterFor(i, _lastSeq[i]);
                _lastSeq[i] = Engine.Log.Count;
                if (events.Count > 0)
                    seat.DeliverEvents(events);
                seat.DeliverSnapshot(SnapshotBuilder.Build(Engine, i));
            }
        }

        private void RouteInput()
        {
            var pending = Engine.PendingInput;
            if (pending == null) return;
            var seat = _seats[pending.PlayerIndex];
            if (seat == null) return;
            _pendingElapsed = 0;

            var snap = new PendingSnap
            {
                Kind = pending.Kind,
                PlayerIndex = pending.PlayerIndex,
                LegalActions = pending.LegalActions,
                Decision = pending.Decision
            };
            seat.OnInputRequested(snap);
        }
    }

    /// <summary>A synchronous bot occupying a seat, paced by a think-delay.</summary>
    public sealed class BotSeat : IHostSeat
    {
        public int PlayerIndex { get; }
        private readonly ISyncAgent _agent;
        private readonly float _thinkDelay;
        private GameHost _host;
        private PendingSnap _pending;
        private float _wait;

        public BotSeat(int playerIndex, ISyncAgent agent, float thinkDelaySeconds = 0.6f)
        {
            PlayerIndex = playerIndex;
            _agent = agent;
            _thinkDelay = thinkDelaySeconds;
        }

        public void Bind(GameHost host) => _host = host;

        public void DeliverEvents(List<GameEvent> filteredEvents) { }
        public void DeliverSnapshot(ClientSnapshot snapshot) { }

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
            PlayerAction action;
            if (pending.Kind == PendingInputKind.Decision)
            {
                action = new SubmitDecisionAction
                {
                    PlayerIndex = PlayerIndex,
                    Answer = _agent.ChooseDecision(_host.Engine, pending.Decision)
                };
            }
            else
            {
                action = _agent.ChooseAction(_host.Engine, new PendingInput
                {
                    Kind = pending.Kind,
                    PlayerIndex = pending.PlayerIndex,
                    LegalActions = pending.LegalActions
                });
            }
            _host.Submit(PlayerIndex, action);
        }
    }
}
