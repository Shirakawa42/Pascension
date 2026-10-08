using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Shards.Research
{
    // Fresh simulator workload, NOT a strength baseline or a training policy.
    // Never reads hidden cards to select actions. Defaults for damage splits are
    // intentionally weak; this probe measures only the visited state distribution.
    internal static class Program
    {
        private sealed class GameResult
        {
            public long Actions;
            public long DecisionActions;
            public long OutOfTurnDecisions;
            public long BranchSum;
            public int MaxBranch;
            public int MaxOptions;
            public int MaxSplitPower;
            public int MaxHand;
            public int Round;
            public int Events;
            public int Winner;
            public bool Capped;
            public ulong Hash;
            public long Allocated;
            public Dictionary<string, long> Contexts = new();
        }

        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "audit")
            {
                ObservationAudit.Run();
                return;
            }
            int games = args.Length > 0 ? int.Parse(args[0]) : 1000;
            int workers = args.Length > 1 ? int.Parse(args[1]) : 1;
            bool snapshots = args.Length > 2 && args[2] == "snapshot";
            int seedOffset = args.Length > 3 ? int.Parse(args[3]) : 0;
            if (games < 1 || workers < 1) throw new ArgumentException("Positive games/workers required");
            ShardsContentRegistry.EnsureRegistered();
            // Warm JIT and registry before timing. No clearing a shared registry in workers.
            for (int i = 0; i < 32; i++) RunGame(1_000_000 + i, snapshots);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            int[] collections = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var results = new GameResult[games];
            var clock = Stopwatch.StartNew();
            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = workers },
                i => results[i] = RunGame(seedOffset + i, snapshots));
            clock.Stop();
            long actions = results.Sum(x => x.Actions);
            long decisionActions = results.Sum(x => x.DecisionActions);
            var contexts = new SortedDictionary<string, long>();
            foreach (var result in results)
                foreach (var item in result.Contexts)
                    contexts[item.Key] = contexts.TryGetValue(item.Key, out long n) ? n + item.Value : item.Value;
            ulong fingerprint = 14695981039346656037UL;
            foreach (var result in results) fingerprint = unchecked((fingerprint ^ result.Hash) * 1099511628211UL);
            var output = new
            {
                workload = "fresh-exercise-driver-v1",
                runtime = Environment.Version.ToString(),
                games, workers, seedOffset, snapshots,
                seconds = clock.Elapsed.TotalSeconds,
                actions,
                actionsPerSecond = actions / clock.Elapsed.TotalSeconds,
                gamesPerSecond = games / clock.Elapsed.TotalSeconds,
                completed = results.Count(x => !x.Capped),
                capped = results.Count(x => x.Capped),
                wins = new[] { results.Count(x => !x.Capped && x.Winner == 0), results.Count(x => !x.Capped && x.Winner == 1) },
                draws = results.Count(x => !x.Capped && x.Winner < 0),
                meanActionsPerGame = (double)actions / games,
                meanRounds = results.Average(x => x.Round),
                meanEventsPerGame = results.Average(x => x.Events),
                meanPriorityBranching = (double)results.Sum(x => x.BranchSum) / Math.Max(1, actions - decisionActions),
                maxPriorityBranching = results.Max(x => x.MaxBranch),
                maxDecisionOptions = results.Max(x => x.MaxOptions),
                maxSplitPower = results.Max(x => x.MaxSplitPower),
                maxHand = results.Max(x => x.MaxHand),
                decisionActions,
                outOfTurnDecisions = results.Sum(x => x.OutOfTurnDecisions),
                allocatedBytesPerAction = (double)results.Sum(x => x.Allocated) / actions,
                genCollections = new[] { GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2] },
                finalStateFingerprint = fingerprint.ToString("X16"),
                contexts
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static GameResult RunGame(int seed, bool snapshots)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var e = new ShardsEngine(ShardsContentRegistry.StandardConfig((ulong)seed,
                new List<PlayerSpec> { new() { Name = "P0", CharacterId = "decima" },
                    new() { Name = "P1", CharacterId = "tetra" } }, ShardsDlc.Duel));
            var rng = new Random(unchecked(seed * 31 + 17));
            var result = new GameResult();
            while (!e.State.GameOver && result.Actions < 20_000 && e.State.Round <= 400)
            {
                var p = e.PendingInput ?? throw new InvalidOperationException($"No pending input, seed {seed}");
                // Public UI snapshot cost is isolated as an explicit optional workload.
                if (snapshots) GC.KeepAlive(ShardsSnapshotBuilder.Build(e, p.PlayerIndex));
                result.MaxHand = Math.Max(result.MaxHand, e.State.Players[p.PlayerIndex].Hand.Count);
                PlayerAction action;
                if (p.Kind == PendingInputKind.Decision)
                {
                    result.DecisionActions++;
                    if (p.PlayerIndex != e.State.TurnPlayerIndex) result.OutOfTurnDecisions++;
                    var request = p.Decision;
                    string context = request.Context ?? "<untagged>";
                    result.Contexts[context] = result.Contexts.TryGetValue(context, out long n) ? n + 1 : 1;
                    result.MaxOptions = Math.Max(result.MaxOptions, request.Options.Count);
                    if (context == "soi.split") result.MaxSplitPower = Math.Max(result.MaxSplitPower, request.Max);
                    action = ChooseDecision(p, rng);
                }
                else
                {
                    result.BranchSum += p.LegalActions.Count;
                    result.MaxBranch = Math.Max(result.MaxBranch, p.LegalActions.Count);
                    action = ChoosePriority(p, rng);
                }
                var submitted = e.Submit(action);
                if (!submitted.Accepted)
                    throw new InvalidOperationException($"Seed {seed}, step {result.Actions}, context {p.Decision?.Context}: {submitted.Error}");
                result.Actions++;
            }
            result.Capped = !e.State.GameOver;
            result.Winner = e.State.WinnerIndex;
            result.Round = e.State.Round;
            result.Events = e.Log.Count;
            result.Hash = e.State.ComputeHash();
            result.Allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return result;
        }

        private static PlayerAction ChoosePriority(PendingInput pending, Random rng)
        {
            // Small fixed schedule to exercise normal purchases and effects. Does not
            // evaluate cards, look ahead, or attempt to optimize play.
            PlayerAction best = null;
            int bestRank = -1, ties = 0;
            foreach (var action in pending.LegalActions)
            {
                int rank = action switch
                {
                    ShardsPlayCardAction => 6,
                    ShardsExhaustAction => 5,
                    ShardsRecruitRelicAction => 5,
                    ShardsTakeDestinyAction => 5,
                    ShardsHeroAbilityAction => 4,
                    ShardsFocusAction => 3,
                    ShardsAttackMonsterAction => 3,
                    ShardsBuyCardAction => 2,
                    ShardsRerollRowAction => 1,
                    ShardsEndTurnAction => 0,
                    _ => -1
                };
                if (rank > bestRank) { bestRank = rank; best = action; ties = 1; }
                else if (rank == bestRank && rank >= 0 && rng.Next(++ties) == 0) best = action;
            }
            return best ?? throw new InvalidOperationException("No non-concede action");
        }

        private static PlayerAction ChooseDecision(PendingInput pending, Random rng)
        {
            var request = pending.Decision;
            var chosen = new List<int>();
            if (request.Context == "soi.split") chosen.AddRange(request.DefaultOptionIds);
            else
            {
                var available = request.Options.Where(o => !o.Disabled).Select(o => o.Id).ToList();
                for (int i = available.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (available[i], available[j]) = (available[j], available[i]);
                }
                int count = rng.Next(request.Min, Math.Min(request.Max, available.Count) + 1);
                chosen.AddRange(available.Take(count));
            }
            return new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex,
                Answer = new DecisionAnswer { DecisionId = request.Id, ChosenOptionIds = chosen } };
        }
    }
}
