using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;

namespace Shards.AI
{
    /// <summary>
    /// Seat-aware draft values from 1,500 frozen September 28 balance-patch games, 75 per
    /// ordered distinct-hero pair. Values describe this gameplay policy and balance
    /// version, not optimal play. Small samples remain noisy; refresh after changes.
    /// Draft by stable definition ID: compact menu positions have no meaning here.
    /// </summary>
    internal static class HeroDraftPolicy
    {
        private static readonly string[] Heroes = { "decima", "tetra", "volos", "kosynwu", "rez" };
        // Seat-0 wins + half draws, multiplied by two. Rows = seat 0, columns = seat 1.
        // Source: approved-relic-pass-snapshot-1500/statistics-1500/games.json (all 1,500 rows).
        private static readonly int[,] Seat0HalfPoints = {
            { 0, 80, 88, 92, 76 },
            { 88, 0, 96, 90, 84 },
            { 78, 76, 0, 74, 74 },
            { 82, 72, 98, 0, 86 },
            { 82, 68, 110, 76, 0 }
        };

        internal static int Choose(IReadOnlyList<DecisionOption> options, int seat, string draftedOpponent)
        {
            if (seat < 0 || seat > 1) throw new ArgumentOutOfRangeException(nameof(seat));
            int opponent = draftedOpponent == null ? -1 : Array.IndexOf(Heroes, draftedOpponent);
            if (draftedOpponent != null && opponent < 0) throw new ArgumentException("Unknown drafted hero");
            int bestId = -1, bestHero = int.MaxValue, bestValue = int.MinValue;
            foreach (var option in options)
            {
                if (option.Disabled) continue;
                int hero = Array.IndexOf(Heroes, option.DefId);
                if (hero < 0) throw new ArgumentException("Unknown legal draft hero");
                if (hero == opponent) continue;
                int value;
                if (opponent >= 0) value = Score(hero, opponent, seat);
                else
                {
                    // First picker anticipates the opponent's strongest response.
                    value = int.MaxValue;
                    foreach (var response in options)
                    {
                        if (response.Disabled) continue;
                        int other = Array.IndexOf(Heroes, response.DefId);
                        if (other < 0) throw new ArgumentException("Unknown legal draft hero");
                        if (other != hero) value = Math.Min(value, Score(hero, other, seat));
                    }
                }
                // Stable identity breaks ties, independent of UI/menu order.
                if (value > bestValue || value == bestValue && hero < bestHero)
                { bestId = option.Id; bestHero = hero; bestValue = value; }
            }
            if (bestId < 0) throw new InvalidOperationException("No legal draft hero");
            return bestId;
        }

        private static int Score(int own, int opponent, int seat) =>
            seat == 0 ? Seat0HalfPoints[own, opponent] : 150 - Seat0HalfPoints[opponent, own];
    }
}
