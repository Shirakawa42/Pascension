using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Shards.AI
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
        // Frozen training/evaluation configuration, independent of machine environment.
        internal const int SplitBranches = 8;
        internal bool HeroFixEnabled => true;
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
        private DecisionRequest _sacrificePreview;
        private int[] _declinedTargets;
        internal readonly CenterKnowledge Knowledge = new();
        internal readonly SupplementKnowledge Supplement = new();
        internal DecisionRequest Decision => _sacrificePreview ?? Engine.PendingInput?.Decision;
        internal bool SacrificePreview => _sacrificePreview != null;
        private readonly List<DecisionOption> _splitOptions = new();
        private bool _lethalSplit;
        private readonly HashSet<int> _shieldedChampionOwners = new();
        internal int Actor => Engine.PendingInput?.PlayerIndex ?? 0;
        internal bool Truncated => !Engine.State.GameOver &&
            (Submissions >= 20_000 || Engine.State.Round > 400 || WrapperSteps >= 100_000);
        internal int VisibleCount => Candidates.Count <= Encoder.MaxActions ? Candidates.Count :
            Math.Min(63, Candidates.Count - Page * 63) + 1;

        internal Action<PlayerAction> SubmitThroughHost;
        internal Adapter(ShardsEngine engine)
        {
            Engine = engine;
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
            if (HeroFixEnabled && c.Action is ShardsHeroAbilityAction && Engine.State.Players[Actor].CharacterId == "kosynwu")
                PreviewSacrifice();
            else if (c.Action != null) Submit(c.Action);
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

        private void Submit(PlayerAction action) => SubmitCore(action, true);

        private void SubmitCore(PlayerAction action, bool rebuild)
        {
            if (SubmitThroughHost == null) throw new InvalidOperationException("AI host is not bound");
            SubmitThroughHost(action);
            if (rebuild) Rebuild();
        }

        internal SubmitResult ApplyExternal(PlayerAction action)
        {
            int logStart = Engine.Log.Count;
            int deckCount = Engine.State.CenterDeck.Count;
            int flood = CenterKnowledge.FloodMask(Engine);
            // Keep the pre-action public request, but update memory only after acceptance.
            int actor = Actor;
            var request = Engine.PendingInput?.Decision;
            int deck0 = Engine.State.Players[0].Deck.Count, deck1 = Engine.State.Players[1].Deck.Count;
            var result = Engine.Submit(action);
            if (!result.Accepted) return result;
            Knowledge.BeforeSubmit(actor, request, action);
            Supplement.BeforeSubmit(deck0, deck1, request, action);
            Submissions++;
            Knowledge.AfterSubmit(Engine, logStart, deckCount, flood);
            Supplement.AfterSubmit(Engine, logStart);
            for (int i = logStart; i < Engine.Log.Count; i++)
                if (Engine.Log[i] is ShardsTurnStartedEvent) _declinedTargets = null;
            _staged = null;
            Selected.Clear();
            SelectionTrace.Clear();
            Rebuild();
            return result;
        }

        private void SubmitSelection()
        {
            if (_sacrificePreview != null)
            {
                int actor = Actor;
                int chosen = Selected.Count == 0 ? -1 : Selected[0];
                _sacrificePreview = null; _staged = null;
                Selected.Clear(); SelectionTrace.Clear();
                if (chosen < 0)
                {
                    _declinedTargets = SacrificeTargets();
                    Rebuild();
                    return;
                }
                // No RNG or game state advanced during the preview. Pay only
                // after committing to a still-legal card; submit both real actions.
                Submit(new ShardsHeroAbilityAction { PlayerIndex = actor });
                var actual = Engine.PendingInput?.Decision;
                if (actual?.Context != "soi.banish" || actual.Max != 1 ||
                    !actual.Options.Exists(o => o.Id == chosen && !o.Disabled))
                    throw new InvalidOperationException("Sacrifice target changed during atomic submission");
                Submit(new SubmitDecisionAction { PlayerIndex = actor,
                    Answer = new DecisionAnswer { DecisionId = actual.Id, ChosenOptionIds = new List<int> { chosen } } });
                return;
            }
            Submit(new SubmitDecisionAction { PlayerIndex = Actor,
                Answer = new DecisionAnswer { DecisionId = _staged.Id, ChosenOptionIds = new List<int>(Selected) } });
        }

        private int[] SacrificeTargets()
        {
            var p = Engine.State.Players[Actor];
            var ids = new int[p.Hand.Count + p.Discard.Count + 1];
            ids[0] = p.Hand.Count; int n = 1;
            foreach (var c in p.Hand) ids[n++] = c.InstanceId;
            foreach (var c in p.Discard) ids[n++] = c.InstanceId;
            return ids;
        }

        private bool CanPreviewSacrifice()
        {
            var p = Engine.State.Players[Actor];
            if (p.Hand.Count + p.Discard.Count == 0) return false;
            if (_declinedTargets == null) return true;
            var current = SacrificeTargets();
            if (current.Length != _declinedTargets.Length) return true;
            for (int i = 0; i < current.Length; i++) if (current[i] != _declinedTargets[i]) return true;
            return false;
        }

        private void PreviewSacrifice()
        {
            if (!CanPreviewSacrifice()) throw new InvalidOperationException("Empty or unchanged declined Sacrifice");
            var p = Engine.State.Players[Actor];
            _sacrificePreview = new DecisionRequest { PlayerIndex = Actor, Kind = DecisionKind.ChooseCards,
                Context = "soi.banish", Title = "Banish a card from your hand or discard pile?", Min = 0, Max = 1 };
            foreach (var c in p.Hand) Add(c, "hand");
            foreach (var c in p.Discard) Add(c, "discard");
            void Add(ShardsCard c, string zone) => _sacrificePreview.Options.Add(
                new DecisionOption(c.InstanceId, c.Def.Name + " (" + zone + ")")
                { CardInstanceId = c.InstanceId, DefId = c.DefId });
            Rebuild();
        }

        private void SettleSplit()
        {
            if (_lethalSplit) { SettleLethalSplit(); return; }
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

        // DecisionOption.Amount is the engine's PUBLIC remaining-defense contract.
        // Never recompute enemy defense from hidden collections. Taunts go first;
        // the opposing player goes last and receives the unspent residual.
        private void InitializeLethalSplit()
        {
            _shieldedChampionOwners.Clear();
            foreach (var p in Engine.State.Players)
                if (p.Champions.Exists(c => c.Def.ShieldsProtectChampions))
                    _shieldedChampionOwners.Add(p.Index);
            var ordered = new List<DecisionOption>();
            foreach (var o in _splitOptions) if (o.CardInstanceId >= 0 && o.Required) ordered.Add(o);
            foreach (var o in _splitOptions) if (o.CardInstanceId >= 0 && !o.Required) ordered.Add(o);
            foreach (var o in _splitOptions) if (o.CardInstanceId < 0) ordered.Add(o);
            _splitOptions.Clear(); _splitOptions.AddRange(ordered);
        }

        private bool SplitBlocked(DecisionOption option)
        {
            foreach (var required in _splitOptions)
            {
                if (!required.Required || required.OwnerIndex != option.OwnerIndex) continue;
                // The engine's gate is the first taunt in the public champion list.
                if (required == option) return false;
                bool assigned = false;
                foreach (var entry in SelectionTrace)
                    if (entry.option == required && entry.amount >= required.Amount) { assigned = true; break; }
                return !assigned;
            }
            return false;
        }

        // Rank the sparse legal amount set (zero plus a lethal interval). Splitting
        // ranks, not numeric distance, guarantees progress even across a large gap.
        internal static List<(int low, int high)> LethalIntervals(int low, int high,
            int threshold, bool overkill, int branches)
        {
            var result = new List<(int, int)>();
            bool zero = low == 0 && high >= 0;
            int begin = Math.Max(Math.Max(1, threshold), low);
            int end = overkill ? high : Math.Min(high, threshold);
            int positive = Math.Max(0, end - begin + 1);
            int count = (zero ? 1 : 0) + positive;
            int Amount(int rank) => zero && rank == 0 ? 0 : begin + rank - (zero ? 1 : 0);
            branches = Math.Min(branches, count);
            for (int i = 0; i < branches; i++)
            {
                var ranks = SplitInterval(0, count - 1, branches, i);
                result.Add((Amount(ranks.low), Amount(ranks.high)));
            }
            return result;
        }

        private List<(int low, int high)> CurrentLethalIntervals()
        {
            var o = _splitOptions[SplitTarget];
            if (SplitBlocked(o)) return new List<(int, int)> { (0, 0) };
            if (o.CardInstanceId < 0) return new List<(int, int)> { (Remaining, Remaining) };
            return LethalIntervals(Low, High, o.Amount,
                _shieldedChampionOwners.Contains(o.OwnerIndex), SplitBranches);
        }

        private void SettleLethalSplit()
        {
            while (true)
            {
                var intervals = CurrentLethalIntervals();
                if (intervals.Count == 0) throw new InvalidOperationException("Lethal split has no completion");
                if (intervals.Count != 1 || intervals[0].low != intervals[0].high) break;
                var option = _splitOptions[SplitTarget];
                int amount = intervals[0].low;
                for (int i = 0; i < amount; i++) Selected.Add(option.Id);
                SelectionTrace.Add((option, _staged.Options.IndexOf(option), amount));
                Remaining -= amount;
                if (++SplitTarget == _splitOptions.Count)
                {
                    if (Selected.Count < _staged.Min) throw new InvalidOperationException("Lethal split violated full assignment");
                    SubmitSelection(); return;
                }
                Low = 0; High = Remaining;
            }
            Rebuild();
        }

        internal void Rebuild()
        {
            // Complete only source-proven dominated decisions. Iteration avoids
            // recursive Rebuild chains when several copied effects are queued.
            while (!Engine.State.GameOver && TryAutomaticReactor()) { }
            Candidates.Clear();
            Page = 0;
            if (Engine.State.GameOver) return;
            var pending = Engine.PendingInput ?? throw new InvalidOperationException("No pending input");
            if (pending.Kind == PendingInputKind.Priority && _sacrificePreview == null)
            {
                foreach (var action in pending.LegalActions)
                {
                    if (HeroFixEnabled && action is ShardsHeroAbilityAction && Engine.State.Players[Actor].CharacterId == "kosynwu" &&
                        !CanPreviewSacrifice()) continue;
                    Candidates.Add(new Candidate { Kind = KindFor(action), Action = action });
                }
                return;
            }
            var request = Decision;
            if (_staged != request)
            {
                if (request.Context != null && !KnownContexts.Contains(request.Context))
                    throw new InvalidOperationException("Unknown decision context " + request.Context);
                _staged = request;
                Knowledge.ObserveDecision(Actor, request);
                Supplement.ObserveDecision(Engine, Actor, request);
                Selected.Clear();
                SelectionTrace.Clear();
                if (request.Context == "soi.split")
                {
                    _splitOptions.Clear();
                    foreach (var option in request.Options) if (!option.Disabled) _splitOptions.Add(option);
                    if (_splitOptions.Count == 0) throw new InvalidOperationException("Empty split");
                    _lethalSplit = Engine.State.Players.Count == 2 &&
                        _splitOptions.Exists(o => o.CardInstanceId >= 0);
                    if (_lethalSplit) InitializeLethalSplit();
                    SplitTarget = 0;
                    Remaining = request.Max;
                    Low = _splitOptions.Count == 1 ? request.Min : 0;
                    High = Remaining;
                    if (_lethalSplit) { SettleLethalSplit(); return; }
                    if (Low == High) { SettleSplit(); return; }
                }
            }
            if (request.Context == "soi.split")
            {
                if (_lethalSplit)
                {
                    foreach (var interval in CurrentLethalIntervals())
                        Candidates.Add(new Candidate { Kind = 15, Option = _splitOptions[SplitTarget],
                            Ordinal = request.Options.IndexOf(_splitOptions[SplitTarget]),
                            Low = interval.low, High = interval.high });
                    return;
                }
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
            if (Candidates.Count == 0) throw new InvalidOperationException($"Decision {request.Context} has no valid completion (minimum {request.Min}, selected {Selected.Count}, options {request.Options.Count})");
        }

        private bool TryAutomaticReactor()
        {
            var d = Decision;
            if (d == null || d.Context != "soi.mode" || d.Kind != DecisionKind.ChooseMode ||
                d.Title != "Reactor Drone: choose one" || d.Min != 1 || d.Max != 1 ||
                d.Options.Count != 2 || Selected.Count != 0) return false;
            var a = d.Options[0]; var b = d.Options[1];
            if (a.Id != 1 || a.Label != "Gain 2 gems" || a.Disabled ||
                b.Id != 2 || b.Label != "Gain 3 gems, then banish this card" || b.Disabled) return false;
            var source = SupplementKnowledge.AuthorizedSource(this);
            // A null/hidden source is uncertainty, never proof of a free copy.
            // A physical drone stays strategic even if already marked for cleanup.
            if (source == null || source.DefId == "reactor_drone_duel") return false;
            Knowledge.ObserveDecision(Actor, d);
            Supplement.ObserveDecision(Engine, Actor, d);
            SubmitCore(new SubmitDecisionAction { PlayerIndex = Actor,
                Answer = new DecisionAnswer { DecisionId = d.Id, ChosenOptionIds = new List<int> { 2 } } }, false);
            return true;
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
