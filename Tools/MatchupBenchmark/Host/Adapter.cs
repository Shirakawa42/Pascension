using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Pascension.Engine.Core;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal struct Candidate
    {
        internal int Kind;
        internal PlayerAction Action;
        internal DecisionOption Option;
        internal int Ordinal, Low, High;
        internal string SortDef, SortLabel;
        internal int SortSlot, SortInstance;
    }

    // No state copy, rollout, gain probe, tactical plan, or value-based filtering.
    // Every integer split and every engine-advertised action remains reachable.
    internal sealed class Adapter
    {
        internal readonly ShardsEngine Engine;
        private readonly Func<PlayerAction, SubmitResult> _submit;
        internal readonly Knowledge Knowledge = new();
        internal readonly List<Candidate> Candidates = new();
        internal readonly List<int> Selected = new();
        internal readonly List<(DecisionOption option, int ordinal, int amount)> SelectionTrace = new();
        internal readonly bool AutomaticSingletons;
        internal readonly List<ShardsCard> EntityBuffer = new(Encoder.Capacity);
        internal readonly Dictionary<int,int> EntityMap = new(Encoder.Capacity);
        internal readonly Dictionary<int,(int ordinal,string def)> KnownSources = new(Encoder.Capacity);
        internal readonly Dictionary<int,int> SourceOrdinals = new(Encoder.Capacity);
        internal IntPtr ObservationAddress;
        internal int EncodedEntities, EncodedOptions, EncodedKnowledge, EncodedTrace, EncodedPlayed, EncodedQueue, EncodedCopy;
        private readonly int[] _initialCounts = new int[Encoder.CardCapacity];
        private string _initialHero0, _initialHero1;
        private bool _initialCached;
        internal int[] InitialCounts()
        {
            string h0=Engine.State.Players[0].CharacterId,h1=Engine.State.Players[1].CharacterId;
            if(!_initialCached||h0!=_initialHero0||h1!=_initialHero1)
            {
                Array.Clear(_initialCounts);foreach(var pair in Engine.InitialCardCounts())_initialCounts[Encoder.CardIndex(pair.Key)]=pair.Value;
                _initialHero0=h0;_initialHero1=h1;_initialCached=true;
            }
            return _initialCounts;
        }
        internal long WrapperSteps, PolicyChoices, Submissions, AutomaticallyApplied;
        internal int Page, SplitTarget, Remaining, Low, High;
        private DecisionRequest _staged;
        private readonly List<DecisionOption> _split = new();
        private Frame _frame;
        private sealed class Frame
        {
            internal int Start;
            internal DecisionRequest Request;
            internal PlayerAction Action;
            internal Knowledge.Before Before;
            internal bool Observed;
        }
        internal int Actor => Engine.PendingInput?.PlayerIndex ?? Engine.State.TurnPlayerIndex;
        internal DecisionRequest Decision => Engine.PendingInput?.Decision;
        internal bool Truncated => !Engine.State.GameOver && (Submissions >= 20000 ||
            Engine.State.Round > 400 || WrapperSteps >= 100000);
        internal int VisibleCount => Candidates.Count <= Encoder.MaxActions ? Candidates.Count :
            Math.Min(Encoder.MaxActions - 1, Candidates.Count - Page * (Encoder.MaxActions - 1)) + 1;
        internal Adapter(ShardsEngine engine, Func<PlayerAction, SubmitResult> submit = null, bool automaticSingletons = false)
        {
            if (engine.State.Players.Count != 2) throw new NotSupportedException("Zero-depth host currently supports two seats");
            Engine = engine; _submit = submit ?? engine.Submit; AutomaticSingletons = automaticSingletons;
            Knowledge.Initialize(engine);
            Rebuild();
        }
        internal Candidate Visible(int index)
        {
            if (index < 0 || index >= VisibleCount) throw new ArgumentOutOfRangeException(nameof(index));
            if (Candidates.Count <= Encoder.MaxActions) return Candidates[index];
            return index == VisibleCount - 1 ? new Candidate { Kind = 14 } :
                Candidates[Page * (Encoder.MaxActions - 1) + index];
        }
        internal void Step(int index)
        {
            if (Engine.State.GameOver || Truncated) throw new InvalidOperationException("Step after terminal/censor");
            PolicyChoices++;
            var candidate = Visible(index);
            StepCore(candidate);
            if (candidate.Kind == 14) return;
            Rebuild();
        }
        private void StepCore(Candidate c)
        {
            WrapperSteps++;
            if (c.Kind == 14) { Page = (Page + 1) % ((Candidates.Count + Encoder.MaxActions - 2) / (Encoder.MaxActions - 1)); return; }
            if (c.Action != null) { ApplyExternal(c.Action); return; }
            if (c.Kind == 15)
            {
                Low = c.Low; High = c.High;
                if (Low == High) SettleSplit();
                return;
            }
            if (c.Kind == 12)
            {
                Selected.Add(c.Option.Id);
                SelectionTrace.Add((c.Option, c.Ordinal, 1));
                if (Selected.Count < _staged.Max) return;
            }
            else if (c.Kind != 13) throw new InvalidOperationException("Unknown wrapper candidate");
            SubmitSelection();
        }
        internal SubmitResult ApplyExternal(PlayerAction action)
        {
            // Incumbent Rebuild can synchronously submit a forced copied effect.
            // Observe accepted parent events BEFORE that nested submission starts.
            var parent = _frame;
            if (parent != null) ObserveFrame(parent);
            var frame = new Frame { Start = Engine.Log.Count, Request = Decision, Action = action,
                Before = Knowledge.BeforeSubmit(Engine, Decision, action) };
            _frame = frame;
            var result = _submit(action);
            if (!result.Accepted) throw new InvalidOperationException("Engine rejected advertised action: " + result.Error);
            ObserveFrame(frame);
            _frame = parent;
            _staged = null; Selected.Clear(); SelectionTrace.Clear();
            if (parent == null) BuildCandidates();
            return result;
        }
        private void ObserveFrame(Frame frame)
        {
            if (frame.Observed) return;
            Knowledge.AfterSubmit(Engine, frame.Start, frame.Request, frame.Action, frame.Before);
            Submissions++; frame.Observed = true;
        }
        private void SubmitSelection() => ApplyExternal(new SubmitDecisionAction { PlayerIndex = Actor,
            Answer = new DecisionAnswer { DecisionId = _staged.Id, ChosenOptionIds = new List<int>(Selected) } });
        private void SettleSplit()
        {
            while (Low == High)
            {
                var option = _split[SplitTarget];
                for (int i = 0; i < Low; i++) Selected.Add(option.Id);
                SelectionTrace.Add((option, _staged.Options.IndexOf(option), Low));
                Remaining -= Low; SplitTarget++;
                if (SplitTarget == _split.Count) { SubmitSelection(); return; }
                Low = SplitTarget == _split.Count - 1 ? Math.Max(0, _staged.Min - Selected.Count) : 0;
                High = Remaining;
            }
        }
        internal void Rebuild()
        {
            BuildCandidates();
            while (AutomaticSingletons && !Engine.State.GameOver && !Truncated && VisibleCount == 1)
            {
                AutomaticallyApplied++; StepCore(Visible(0)); BuildCandidates();
            }
        }
        private void BuildCandidates()
        {
            Candidates.Clear(); Page = 0;
            if (Engine.State.GameOver || Truncated) return;
            var pending = Engine.PendingInput ?? throw new InvalidOperationException("Missing pending input");
            if (pending.Kind == PendingInputKind.Priority)
            {
                foreach (var action in pending.LegalActions) Candidates.Add(new Candidate { Kind = Shards.AI.Adapter.KindFor(action), Action = action });
                SortCandidates();
                return;
            }
            var request = Decision;
            if (_staged != request)
            {
                _staged = request; Selected.Clear(); SelectionTrace.Clear();
                Knowledge.ObserveDecision(Actor, request);
                if (request.Context == "soi.split")
                {
                    _split.Clear(); _split.AddRange(request.Options.Where(o => !o.Disabled));
                    if (_split.Count == 0) throw new InvalidOperationException("Empty damage split");
                    SplitTarget = 0; Remaining = request.Max;
                    Low = _split.Count == 1 ? request.Min : 0; High = Remaining;
                }
            }
            if (request.Context == "soi.split")
            {
                int middle = Low + (High - Low) / 2;
                Candidates.Add(new Candidate { Kind = 15, Option = _split[SplitTarget], Ordinal = request.Options.IndexOf(_split[SplitTarget]), Low = Low, High = middle });
                if (middle < High) Candidates.Add(new Candidate { Kind = 15, Option = _split[SplitTarget], Ordinal = request.Options.IndexOf(_split[SplitTarget]), Low = middle + 1, High = High });
                return;
            }
            for (int i = 0; Selected.Count < request.Max && i < request.Options.Count; i++)
            {
                var option = request.Options[i];
                if (!option.Disabled && !Selected.Contains(option.Id)) Candidates.Add(new Candidate { Kind = 12, Option = option, Ordinal = i });
            }
            if (Selected.Count >= request.Min) Candidates.Add(new Candidate { Kind = 13 });
            if (!request.Ordered) SortCandidates();
            if (Candidates.Count == 0) throw new InvalidOperationException("Decision has no valid completion");
        }
        private void SortCandidates()
        {
            for(int i=0;i<Candidates.Count;i++)
            {
                var c=Candidates[i];
            string id = c.Option?.DefId;
            int instance = c.Action switch { ShardsPlayCardAction a => a.CardInstanceId, ShardsExhaustAction a => a.CardInstanceId,
                ShardsAttackMonsterAction a => a.CardInstanceId, ShardsTakeDestinyAction a => a.CardInstanceId,
                ShardsRecruitRelicAction a => a.CardInstanceId, _ => -1 };
            if (instance >= 0) id = Engine.State.FindCard(instance)?.DefId;
            int slot = c.Action is ShardsBuyCardAction buy ? buy.SlotIndex : c.Action is ShardsRerollRowAction reroll ? reroll.SlotIndex : -1;
            if (slot >= 0) id = Engine.State.CenterRow[slot]?.DefId;
                c.SortDef=id;c.SortSlot=slot;c.SortInstance=instance;c.SortLabel=c.Option?.Label;
                Candidates[i]=c;
            }
            Candidates.Sort((a,b)=>
            {
                int order=a.Kind.CompareTo(b.Kind);if(order!=0)return order;
                order=string.CompareOrdinal(a.SortDef,b.SortDef);if(order!=0)return order;
                order=a.SortSlot.CompareTo(b.SortSlot);if(order!=0)return order;
                order=string.CompareOrdinal(a.SortLabel,b.SortLabel);if(order!=0)return order;
                order=a.SortInstance.CompareTo(b.SortInstance);if(order!=0)return order;
                return a.Ordinal.CompareTo(b.Ordinal);
            });
        }
        internal int ExerciseChoice()
        {
            int best = 0, rank = int.MinValue;
            for (int i = 0; i < VisibleCount; i++)
            {
                int r = Visible(i).Kind switch { 0 => 9, 4 or 6 or 7 => 8, 3 => 6, 9 => 5, 5 => 4,
                    1 or 2 => 3, 8 => -1, 10 => 0, 11 => -100, 12 => 2, 13 => 1, 15 => 2, 14 => -50, _ => -100 };
                if (r > rank) { rank = r; best = i; }
            }
            return best;
        }
    }
}
