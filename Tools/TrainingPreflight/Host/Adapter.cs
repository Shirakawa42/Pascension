using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Shards.Preflight
{
    internal struct Candidate
    {
        internal int Kind;
        internal PlayerAction Action;
        internal DecisionOption Option;
        internal int Ordinal;
        internal int Low;
        internal int High;
    }

    internal sealed class Adapter
    {
        internal static readonly int SplitBranches = ReadSplitBranches();
        private static int ReadSplitBranches()
        {
            string value = Environment.GetEnvironmentVariable("SHARDS_SPLIT_BRANCHES");
            int branches = value == null ? 2 : int.Parse(value);
            if (branches < 2 || branches > 64) throw new ArgumentException("SHARDS_SPLIT_BRANCHES must be 2..64");
            return branches;
        }
        private static readonly HashSet<string> KnownContexts = new(StringComparer.Ordinal)
        {
            "soi.banish", "soi.copy", "soi.destroy", "soi.keepfast", "soi.mode", "soi.removeshop",
            "soi.return", "soi.reveal", "soi.target", "soi.tutor", "soi.warp", "soi.confirm",
            "soi.defiant", "soi.destiny", "soi.relic", "soi.reset", "soi.reorder", "soi.scry",
            "soi.volos", "soi.discard", "soi.recruit", "soi.herodraft", "soi.maglev", "soi.shields", "soi.split"
        };
        internal ShardsEngine Engine;
        internal readonly List<Candidate> Candidates = new();
        internal readonly List<int> Selected = new();
        internal readonly List<(DecisionOption option, int ordinal, int amount)> SelectionTrace = new();
        internal long WrapperSteps;
        internal long Submissions;
        internal int Page;
        internal int SplitTarget;
        internal int Remaining;
        internal int Low;
        internal int High;
        private DecisionRequest _staged;
        private readonly List<DecisionOption> _splitOptions = new();
        internal int Actor => Engine.PendingInput?.PlayerIndex ?? 0;
        internal bool Truncated => !Engine.State.GameOver &&
            (Submissions >= 20_000 || Engine.State.Round > 400 || WrapperSteps >= 100_000);
        internal int VisibleCount => Candidates.Count <= Encoder.MaxActions ? Candidates.Count :
            Math.Min(63, Candidates.Count - Page * 63) + 1;

        internal Adapter(ulong seed)
        {
            Engine = new ShardsEngine(ShardsContentRegistry.StandardConfig(seed,
                new List<PlayerSpec> { new() { Name = "P0", CharacterId = "decima" },
                    new() { Name = "P1", CharacterId = "tetra" } }, ShardsDlc.Duel));
            Rebuild();
        }

        internal Candidate Visible(int index)
        {
            if (index < 0 || index >= VisibleCount) throw new ArgumentOutOfRangeException(nameof(index));
            if (Candidates.Count <= Encoder.MaxActions) return Candidates[index];
            return index == VisibleCount - 1 ? PageCandidate : Candidates[Page * 63 + index];
        }

        private static readonly Candidate PageCandidate = new() { Kind = 14 };
        internal void Step(int index)
        {
            if (Engine.State.GameOver || Truncated) throw new InvalidOperationException("Step after episode ended");
            var c = Visible(index);
            WrapperSteps++;
            if (c.Kind == 14)
            {
                Page = (Page + 1) % ((Candidates.Count + 62) / 63);
                return;
            }
            if (c.Action != null) Submit(c.Action);
            else if (c.Kind == 15)
            {
                Low = c.Low;
                High = c.High;
                SettleSplit();
            }
            else if (c.Kind == 13) SubmitSelection();
            else if (c.Kind == 12)
            {
                Selected.Add(c.Option.Id);
                SelectionTrace.Add((c.Option, c.Ordinal, 1));
                if (Selected.Count == _staged.Max) SubmitSelection();
                else Rebuild();
            }
            else throw new InvalidOperationException("Unknown wrapper candidate");
        }

        private void Submit(PlayerAction action)
        {
            var result = Engine.Submit(action);
            if (!result.Accepted)
                throw new InvalidOperationException($"Rejected {action.Describe()}: {result.Error}");
            Submissions++;
            _staged = null;
            Selected.Clear();
            SelectionTrace.Clear();
            Rebuild();
        }

        private void SubmitSelection() => Submit(new SubmitDecisionAction
        {
            PlayerIndex = Actor,
            Answer = new DecisionAnswer { DecisionId = _staged.Id, ChosenOptionIds = new List<int>(Selected) }
        });

        private void SettleSplit()
        {
            while (Low == High)
            {
                var option = _splitOptions[SplitTarget];
                for (int i = 0; i < Low; i++) Selected.Add(option.Id);
                SelectionTrace.Add((option, SplitTarget, Low));
                Remaining -= Low;
                SplitTarget++;
                if (SplitTarget == _splitOptions.Count)
                {
                    SubmitSelection();
                    return;
                }
                Low = 0;
                High = Remaining;
                if (SplitTarget == _splitOptions.Count - 1)
                    Low = Math.Max(0, _staged.Min - Selected.Count);
            }
            Rebuild();
        }

        internal void Rebuild()
        {
            Candidates.Clear();
            Page = 0;
            if (Engine.State.GameOver) return;
            var pending = Engine.PendingInput ?? throw new InvalidOperationException("No pending input");
            if (pending.Kind == PendingInputKind.Priority)
            {
                foreach (var action in pending.LegalActions)
                    Candidates.Add(new Candidate { Kind = KindFor(action), Action = action });
                return;
            }
            var request = pending.Decision;
            if (_staged != request)
            {
                if (request.Context != null && !KnownContexts.Contains(request.Context))
                    throw new InvalidOperationException("Unknown decision context " + request.Context);
                _staged = request;
                Selected.Clear();
                SelectionTrace.Clear();
                if (request.Context == "soi.split")
                {
                    _splitOptions.Clear();
                    foreach (var option in request.Options) if (!option.Disabled) _splitOptions.Add(option);
                    if (_splitOptions.Count == 0) throw new InvalidOperationException("Empty split");
                    SplitTarget = 0;
                    Remaining = request.Max;
                    Low = _splitOptions.Count == 1 ? request.Min : 0;
                    High = Remaining;
                    if (Low == High) { SettleSplit(); return; }
                }
            }
            if (request.Context == "soi.split")
            {
                int branches = Math.Min(SplitBranches, High - Low + 1);
                for (int i = 0; i < branches; i++)
                {
                    var interval = SplitInterval(Low, High, branches, i);
                    Candidates.Add(new Candidate { Kind = 15, Option = _splitOptions[SplitTarget],
                        Ordinal = SplitTarget, Low = interval.low, High = interval.high });
                }
                return;
            }
            for (int i = 0; i < request.Options.Count; i++)
            {
                var option = request.Options[i];
                if (!option.Disabled && !Selected.Contains(option.Id))
                    Candidates.Add(new Candidate { Kind = 12, Option = option, Ordinal = i });
            }
            if (Selected.Count >= request.Min) Candidates.Add(new Candidate { Kind = 13 });
            if (Candidates.Count == 0) throw new InvalidOperationException("Decision has no valid completion");
        }

        internal static (int low, int high) SplitInterval(int low, int high, int branches, int index)
        {
            int count = high - low + 1;
            return (low + (int)((long)count * index / branches),
                low + (int)((long)count * (index + 1) / branches) - 1);
        }

        internal static int KindFor(PlayerAction action) => action switch
        {
            ShardsPlayCardAction => 0,
            ShardsBuyCardAction buy => buy.FastPlay ? 2 : 1,
            ShardsFocusAction => 3,
            ShardsExhaustAction => 4,
            ShardsAttackMonsterAction => 5,
            ShardsTakeDestinyAction => 6,
            ShardsRecruitRelicAction => 7,
            ShardsRerollRowAction => 8,
            ShardsHeroAbilityAction => 9,
            ShardsEndTurnAction => 10,
            ConcedeAction => 11,
            _ => throw new InvalidOperationException("Unhandled priority action " + action.GetType().Name)
        };

        // Bounded exercise policy only. Uses legal/public categories, not hidden state.
        internal int ExerciseChoice(Random rng)
        {
            int chosen = 0, best = -100, ties = 0;
            for (int i = 0; i < VisibleCount; i++)
            {
                var c = Visible(i);
                int rank = c.Kind switch
                {
                    0 => 6, 4 or 6 or 7 => 5, 9 => 4, 3 or 5 => 3,
                    1 or 2 => 2, 8 => 1, 10 => 0, 11 => -90,
                    12 or 13 or 15 => 1, 14 => -50, _ => -100
                };
                if (rank > best) { best = rank; chosen = i; ties = 1; }
                else if (rank == best && rng.Next(++ties) == 0) chosen = i;
            }
            return chosen;
        }
    }
}
