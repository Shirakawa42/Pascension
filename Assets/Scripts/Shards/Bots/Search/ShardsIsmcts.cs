using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Single-Observer ISMCTS over the forkable SoI engine. One tree from the
    /// searching player's perspective; each iteration forks the live engine at its
    /// (priority) root, DETERMINIZES the hidden zones, then descends by REAL Submit
    /// calls on the clone — so mid-tree decisions, effect chains and shuffles all run
    /// through the actual rules with zero extra modeling. UCB1 with availability
    /// counts; rollouts are ε-greedy tuned-model playouts to terminal.</summary>
    public sealed class ShardsIsmcts
    {
        private sealed class Child
        {
            public PlayerAction Action;       // template; decision ids re-stamped on submit
            public List<int> AnswerIds;       // decision answers: the chosen option multiset
            public Node Node = new();
            public int Visits;
            public int Availability;
            public double[] Rewards;          // per-seat win scores
            public double Prior;
        }

        private sealed class Node
        {
            public readonly Dictionary<string, Child> Children = new();
        }

        private readonly ShardsEngine _live;
        private readonly int _viewer;
        private readonly ShardsValueModel _model;
        private readonly ShardsSearchConfig _config;
        private readonly DeterministicRng _rng;
        private readonly List<(Node Node, Child Child, int Actor)> _path = new();

        public int IterationsRun { get; private set; }

        public ShardsIsmcts(ShardsEngine live, int viewer, ShardsValueModel model,
            ShardsSearchConfig config, ulong seed)
        {
            _live = live;
            _viewer = viewer;
            _model = model;
            _config = config;
            _rng = new DeterministicRng(seed, 17);
        }

        /// <summary>Search from the live engine's current PRIORITY point and return the
        /// most-visited root action.</summary>
        public PlayerAction Search()
        {
            var root = new Node();
            var sw = _config.Mode == ShardsSearchConfig.BudgetMode.WallClock ? Stopwatch.StartNew() : null;
            int players = _live.State.Players.Count;

            IterationsRun = 0;
            for (int iter = 0; iter < _config.Iterations; iter++)
            {
                if (sw != null && iter % 8 == 0 && sw.Elapsed.TotalSeconds >= _config.WallClockSeconds)
                    break;
                RunIteration(root, players);
                IterationsRun++;
            }

            Child best = null;
            foreach (var child in root.Children.Values)
                if (best == null || child.Visits > best.Visits)
                    best = child;
            return best?.Action ?? _model.ChooseAction(_live, _viewer);
        }

        private void RunIteration(Node root, int players)
        {
            ulong iterSeed = ((ulong)_rng.NextUInt() << 32) | _rng.NextUInt();
            var clone = _live.Fork(rngReseed: iterSeed | 1UL, quiet: true);
            ShardsDeterminizer.Sample(clone.State, _viewer, clone.State.Rng);

            _path.Clear();
            var node = root;
            int submits = 0;
            bool expanded = false;

            while (!clone.State.GameOver && submits < _config.MaxIterationSubmits)
            {
                var pending = clone.PendingInput;
                if (pending == null) break;

                var available = AvailableChildren(clone, pending, node);
                if (available.Count == 0) break;

                Child pick = null;
                foreach (var child in available)
                {
                    child.Availability++;
                    if (child.Visits == 0 && (pick == null || child.Prior > pick.Prior))
                        pick = child; // expand unvisited edges best-prior first
                }
                if (pick == null)
                    pick = SelectUcb(available, pending.PlayerIndex);

                Submit(clone, pending, pick);
                submits++;
                _path.Add((node, pick, pending.PlayerIndex));
                if (pick.Visits == 0)
                {
                    expanded = true;
                    node = pick.Node;
                    break;
                }
                node = pick.Node;
            }

            // Rollout from the expansion point (or terminal) with the ε-greedy model.
            if (expanded)
                Rollout(clone, ref submits);

            var scores = Score(clone, players);
            foreach (var (_, child, _) in _path)
            {
                child.Visits++;
                child.Rewards ??= new double[players];
                for (int p = 0; p < players; p++)
                    child.Rewards[p] += scores[p];
            }
        }

        private List<Child> AvailableChildren(ShardsEngine clone, PendingInput pending, Node node)
        {
            var available = new List<Child>();
            if (pending.Kind == PendingInputKind.Priority)
            {
                foreach (var action in pending.LegalActions)
                {
                    if (action is ConcedeAction) continue;
                    string key = KeyOf(action);
                    if (!node.Children.TryGetValue(key, out var child))
                    {
                        child = new Child
                        {
                            Action = action,
                            Prior = Prior(clone, pending.PlayerIndex, action)
                        };
                        node.Children[key] = child;
                    }
                    else
                    {
                        child.Action = action; // refresh to this clone's instance
                    }
                    available.Add(child);
                }
            }
            else
            {
                foreach (var ids in ShardsDecisionCandidates.Generate(clone, pending.Decision, _model))
                {
                    string key = AnswerKey(ids);
                    if (!node.Children.TryGetValue(key, out var child))
                    {
                        child = new Child { AnswerIds = ids };
                        node.Children[key] = child;
                    }
                    available.Add(child);
                }
            }
            return available;
        }

        private double Prior(ShardsEngine clone, int playerIndex, PlayerAction action)
        {
            double score = _model.ScoreAction(clone, clone.State.Players[playerIndex], action);
            return score <= double.MinValue ? 0 : score / 4000.0; // ladder scale → ~[0,1]
        }

        private Child SelectUcb(List<Child> available, int actor)
        {
            Child best = null;
            double bestScore = double.MinValue;
            foreach (var child in available)
            {
                double mean = child.Rewards == null ? 0.5 : child.Rewards[actor] / child.Visits;
                double explore = _config.Ucb * Math.Sqrt(Math.Log(Math.Max(2, child.Availability)) / child.Visits);
                double bias = _config.ProgressiveBias * child.Prior / (1 + child.Visits);
                double score = mean + explore + bias;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = child;
                }
            }
            return best;
        }

        private void Submit(ShardsEngine clone, PendingInput pending, Child child)
        {
            PlayerAction action;
            if (child.AnswerIds != null)
            {
                var answer = new DecisionAnswer { DecisionId = pending.Decision.Id };
                answer.ChosenOptionIds.AddRange(child.AnswerIds);
                action = new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer };
            }
            else
            {
                action = child.Action;
            }

            if (!clone.Submit(action).Accepted)
            {
                // Stale edge on this determinization — fall back to the safe default so
                // the iteration continues instead of dying.
                var fallback = Pascension.Core.DefaultActions.For(ToSnap(clone.PendingInput));
                clone.Submit(fallback);
            }
        }

        private void Rollout(ShardsEngine clone, ref int submits)
        {
            while (!clone.State.GameOver && submits < _config.MaxIterationSubmits)
            {
                var pending = clone.PendingInput;
                if (pending == null) return;
                PlayerAction action;
                if (pending.Kind == PendingInputKind.Decision)
                {
                    action = new SubmitDecisionAction
                    {
                        PlayerIndex = pending.PlayerIndex,
                        Answer = _model.ChooseAnswer(clone, pending.Decision)
                    };
                }
                else if (_rng.Next(10000) < (int)(_config.RolloutEpsilon * 10000))
                {
                    var legal = pending.LegalActions;
                    var usable = legal.FindAll(a => a is not ConcedeAction);
                    action = usable.Count > 0 ? usable[_rng.Next(usable.Count)] : legal[0];
                }
                else
                {
                    action = _model.ChooseAction(clone, pending.PlayerIndex);
                }

                if (!clone.Submit(action).Accepted &&
                    !clone.Submit(Pascension.Core.DefaultActions.For(ToSnap(clone.PendingInput))).Accepted)
                    return;
                submits++;
            }
        }

        private double[] Score(ShardsEngine clone, int players)
        {
            var scores = new double[players];
            if (clone.State.GameOver)
            {
                if (clone.State.WinnerIndex < 0)
                    for (int p = 0; p < players; p++) scores[p] = 0.5;
                else
                    scores[clone.State.WinnerIndex] = 1.0;
            }
            else
            {
                // Iteration hit the submit guard (extremely rare): neutral score.
                for (int p = 0; p < players; p++) scores[p] = 0.5;
            }
            return scores;
        }

        private static Pascension.Engine.Serialization.PendingSnap ToSnap(PendingInput pending) => new()
        {
            Kind = pending.Kind,
            PlayerIndex = pending.PlayerIndex,
            LegalActions = pending.LegalActions,
            Decision = pending.Decision
        };

        private static string KeyOf(PlayerAction action) => action switch
        {
            ShardsPlayCardAction a => "p" + a.CardInstanceId,
            ShardsBuyCardAction a => "b" + a.SlotIndex + (a.FastPlay ? "f" : ""),
            ShardsFocusAction => "focus",
            ShardsExhaustAction a => "x" + a.CardInstanceId,
            ShardsAttackMonsterAction a => "m" + a.CardInstanceId,
            ShardsTakeDestinyAction a => "d" + a.CardInstanceId,
            ShardsRecruitRelicAction a => "r" + a.CardInstanceId,
            ShardsEndTurnAction => "end",
            _ => action.GetType().Name
        };

        private static string AnswerKey(List<int> ids)
        {
            var sorted = new List<int>(ids);
            sorted.Sort();
            var sb = new StringBuilder("a");
            foreach (int id in sorted)
                sb.Append(':').Append(id);
            return sb.ToString();
        }
    }
}
