using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Shards.Content;
using Shards.Engine;

namespace Shards.AI
{
    /// <summary>Host-facing engine with public decision memory shared by human and policy submissions.</summary>
    public sealed class SparsePolicyEngine : IEngineAdapter
    {
        private readonly ShardsEngineAdapter _inner;
        private readonly Adapter _adapter;
        private readonly SparseFrozenPolicy _policy;
        private readonly Random _sampling;
        private readonly bool _tacticalSearch;
        private readonly Lookahead _lookahead;
        private readonly int _inferenceWorkers;
        private Task<int> _pendingSearch;
        private Adapter _planningSnapshot;
        private long _planningStep, _planningSubmission;
        private int _fallback;
        private readonly float[] _obs = new float[3328], _candidates = new float[2048], _mask = new float[64];
        public SparsePolicyEngine(ShardsConfig config, SparseFrozenPolicy policy, int samplingSeed, bool tacticalSearch = false)
            : this(config, policy, samplingSeed, tacticalSearch, null) { }
        public SparsePolicyEngine(ShardsConfig config, SparseFrozenPolicy policy, int samplingSeed, bool tacticalSearch, PolicySearchSettings searchSettings)
        {
            ShardsContentRegistry.EnsureRegistered(); Encoder.Initialize();
            _inner = new ShardsEngineAdapter(config); _adapter = new Adapter(_inner.Inner);
            _policy = policy; _sampling = new Random(samplingSeed); _tacticalSearch = tacticalSearch;
            var settings = (searchSettings ?? new PolicySearchSettings()).ValidatedCopy();
            _inferenceWorkers = settings.Workers;
            _lookahead = new Lookahead(settings, Infer);
        }
        public void BindSubmit(Action<PlayerAction> submit) => _adapter.SubmitThroughHost = submit;
        /// <summary>Pollable gameplay entry point. Search works on a private copy on
        /// a worker thread; only the accepted action reaches the host/main thread.</summary>
        public bool TryStepPolicy()
        {
            if (_pendingSearch != null)
            {
                if (!_pendingSearch.IsCompleted) return false;
                var completed = _pendingSearch;
                _pendingSearch = null;
                int action = completed.GetAwaiter().GetResult();
                if (GameOver || _planningStep != _adapter.WrapperSteps ||
                    _planningSubmission != _adapter.Submissions)
                { _planningSnapshot = null; return false; }
                _adapter.Step(action >= 0 ? action : _fallback);
                return true;
            }
            if (!_tacticalSearch || !CanPlan())
            { StepPolicy(); return true; }
            PreparePolicy();
            return false;
        }
        /// <summary>Start thinking during presentation delay, without taking an action.</summary>
        public void PreparePolicy()
        {
            if (!_tacticalSearch || _pendingSearch != null || GameOver ||
                !CanPlan()) return;
            _fallback = SamplePolicy();
            var snapshot = TacticalSearch.PublicWorld(_adapter, 713101);
            TacticalSearch.TransferPlan(_planningSnapshot, snapshot);
            _lookahead.TransferMacroPlan(_planningSnapshot, snapshot);
            _planningSnapshot = snapshot;
            _planningStep = _adapter.WrapperSteps;
            _planningSubmission = _adapter.Submissions;
            int fallback = _fallback;
            _pendingSearch = Task.Run(() => Plan(snapshot, fallback));
        }
        private bool CanPlan() => _adapter.VisibleCount > 1 && _adapter.Decision?.Context != "soi.herodraft";
        private Prediction[] Infer(Adapter[] positions)
        {
            var result = new Prediction[positions.Length];
            Parallel.For(0, positions.Length, new ParallelOptions { MaxDegreeOfParallelism = _inferenceWorkers }, i =>
            {
                var obs = new float[3328]; var candidates = new float[2048]; var mask = new float[64];
                Encoder.Encode(positions[i], obs, candidates, mask);
                var p = _policy.Probabilities(obs, candidates, mask, out float value);
                result[i] = new Prediction { P = p, V = value };
            });
            return result;
        }
        private int Plan(Adapter position, int fallback)
        {
            var positions = new[] { position };
            return _lookahead.Choose(positions, Infer(positions), new[] { fallback }, new[] { true })[0];
        }
        private int SamplePolicy()
        {
            Encoder.Encode(_adapter, _obs, _candidates, _mask);
            var probabilities = _policy.Probabilities(_obs, _candidates, _mask, out _);
            double draw = _sampling.NextDouble(); int chosen = -1;
            for (int i = 0; i < 64; i++) if (_mask[i] != 0)
            {
                chosen = i; draw -= probabilities[i]; if (draw < 0) break;
            }
            if (chosen < 0) throw new InvalidOperationException("Policy has no legal choice");
            return chosen;
        }
        /// <summary>One bounded wrapper choice; the host handles every real action and filtered broadcast.</summary>
        public void StepPolicy()
        {
            if (_adapter.Decision?.Context == "soi.herodraft")
            {
                if (_adapter.Engine.State.Players.Count != 2)
                    throw new InvalidOperationException("The frozen AI supports only 1v1");
                // Duel drafts seat 1 first. Only seat 0 has an already-public opponent pick.
                string opponent = _adapter.Actor == 0 ? _adapter.Engine.State.Players[1].CharacterId : null;
                int optionId = HeroDraftPolicy.Choose(_adapter.Decision.Options, _adapter.Actor, opponent);
                for (int i = 0; i < _adapter.VisibleCount; i++)
                    if (_adapter.Visible(i).Option?.Id == optionId) { _adapter.Step(i); return; }
                throw new InvalidOperationException("Draft selection is not a legal policy candidate");
            }
            int chosen = SamplePolicy();
            if (_tacticalSearch && CanPlan())
            {
                chosen = Plan(_adapter, chosen);
            }
            _adapter.Step(chosen);
        }
        public SubmitResult Submit(PlayerAction action) => _adapter.ApplyExternal(action);
        public PendingSnap PendingInput => _inner.PendingInput;
        public List<GameEvent> FilterEventsFor(int playerIndex, int sinceSeq) => _inner.FilterEventsFor(playerIndex, sinceSeq);
        public int EventCount => _inner.EventCount;
        public SnapshotBase BuildSnapshot(int playerIndex) => _inner.BuildSnapshot(playerIndex);
        public bool GameOver => _inner.GameOver;
        public int WinnerIndex => _inner.WinnerIndex;
        public PlayerAction DefaultActionFor(PendingSnap pending) => _inner.DefaultActionFor(pending);
    }
}
