using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Fitness engine for the tuner: a candidate's winrate against the
    /// reference pool (60% current champion, 20% frozen heuristic anchor, 20%
    /// historical champion), on mirrored seat-swapped seed pairs with common random
    /// numbers across the whole generation. Ties score 0.5.</summary>
    public sealed class Tournament
    {
        public enum OpponentKind { Champion, Heuristic, Historical }

        public sealed class Matchup
        {
            public ulong Seed;
            public OpponentKind Opponent;
            public bool CandidateFirst;
            public string CharA, CharB; // seat order
        }

        private readonly int _threads;
        private readonly IReadOnlyList<string> _characters;

        public Tournament(int threads)
        {
            _threads = threads;
            _characters = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
        }

        /// <summary>The generation's shared schedule (common random numbers: every
        /// candidate plays the exact same games).</summary>
        public List<Matchup> BuildSchedule(int gamesPerCandidate, ulong genSeed)
        {
            var schedule = new List<Matchup>(gamesPerCandidate);
            int pairs = gamesPerCandidate / 2;
            int championPairs = (int)Math.Round(pairs * 0.6);
            int heuristicPairs = (int)Math.Round(pairs * 0.2);
            var rng = new Pascension.Engine.Core.DeterministicRng(genSeed, 991);
            for (int p = 0; p < pairs; p++)
            {
                var kind = p < championPairs ? OpponentKind.Champion
                    : p < championPairs + heuristicPairs ? OpponentKind.Heuristic
                    : OpponentKind.Historical;
                ulong seed = genSeed * 100000 + (ulong)p;
                int a = rng.Next(_characters.Count);
                int b = rng.Next(_characters.Count - 1);
                if (b >= a) b++;
                string charA = _characters[a], charB = _characters[b];
                schedule.Add(new Matchup { Seed = seed, Opponent = kind, CandidateFirst = true, CharA = charA, CharB = charB });
                schedule.Add(new Matchup { Seed = seed, Opponent = kind, CandidateFirst = false, CharA = charB, CharB = charA });
            }
            return schedule;
        }

        /// <summary>Evaluate every candidate on the shared schedule. Returns fitness
        /// (mean win score) per candidate. Parallel over (candidate × game).</summary>
        public double[] Evaluate(
            IReadOnlyList<ShardsValueModel> candidates,
            ShardsValueModel champion,
            ShardsValueModel historical,
            List<Matchup> schedule,
            CancellationToken ct)
        {
            var scores = new double[candidates.Count];
            var counts = new int[candidates.Count];
            var work = new List<(int Candidate, Matchup Game)>(candidates.Count * schedule.Count);
            for (int c = 0; c < candidates.Count; c++)
                foreach (var m in schedule)
                    work.Add((c, m));

            object sync = new();
            Parallel.ForEach(work,
                new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
                item =>
                {
                    double score = PlayOne(candidates[item.Candidate], champion, historical, item.Game);
                    lock (sync)
                    {
                        scores[item.Candidate] += score;
                        counts[item.Candidate]++;
                    }
                });

            for (int c = 0; c < candidates.Count; c++)
                scores[c] = counts[c] == 0 ? 0 : scores[c] / counts[c];
            return scores;
        }

        /// <summary>One candidate game vs the scheduled opponent; returns the
        /// candidate's win score (1 / 0.5 / 0).</summary>
        public double PlayOne(ShardsValueModel candidate, ShardsValueModel champion,
            ShardsValueModel historical, Matchup game)
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "A", CharacterId = game.CharA },
                new() { Name = "B", CharacterId = game.CharB }
            };
            var adapter = new ShardsEngineAdapter(
                ShardsContentRegistry.StandardConfig(game.Seed, specs, SimConfig.AllDlc));

            int candidateSeat = game.CandidateFirst ? 0 : 1;
            var seats = new IBotAgent[2];
            for (int seat = 0; seat < 2; seat++)
            {
                if (seat == candidateSeat)
                    seats[seat] = new ShardsGreedyEvalBot(game.Seed * 100 + (ulong)seat, adapter.Inner, candidate);
                else
                    seats[seat] = game.Opponent switch
                    {
                        OpponentKind.Heuristic => new ShardsHeuristicBot(game.Seed * 100 + (ulong)seat, adapter.Inner),
                        OpponentKind.Historical => new ShardsGreedyEvalBot(game.Seed * 100 + (ulong)seat, adapter.Inner, historical),
                        _ => new ShardsGreedyEvalBot(game.Seed * 100 + (ulong)seat, adapter.Inner, champion)
                    };
            }

            int guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
            {
                var pending = adapter.PendingInput;
                if (pending == null) return 0; // stall counts as a loss for the candidate
                var action = seats[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    return 0;
            }
            if (!adapter.GameOver) return 0;
            return adapter.WinnerIndex < 0 ? 0.5 : adapter.WinnerIndex == candidateSeat ? 1 : 0;
        }
    }
}
