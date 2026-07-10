using System;
using System.Collections.Generic;

namespace Pascension.Game
{
    /// <summary>
    /// Hand-off blackboard between the menu scene and the game scene. The menu fills it,
    /// GameBootstrap reads it. Plain statics survive a scene load; nothing here persists
    /// across app restarts.
    /// </summary>
    public static class MatchSetup
    {
        /// <summary>Which game the next solo match runs ("pascension" | "shards").</summary>
        public static string GameId = "pascension";

        /// <summary>DLC bitmask for games that have toggleable expansions.</summary>
        public static int DlcFlags;

        public static string PlayerHeroId = "ignis";
        public static string PlayerName = "You";
        public static List<OpponentSetup> Opponents = new List<OpponentSetup>();
        public static ulong Seed;

        /// <summary>True once the menu has configured a match.</summary>
        public static bool Configured;

        /// <summary>
        /// Fills sensible defaults so the Game scene is playable when opened directly
        /// in the editor (bypassing the menu).
        /// </summary>
        public static void EnsureDefaults()
        {
            if (string.IsNullOrEmpty(PlayerHeroId)) PlayerHeroId = "ignis";
            if (string.IsNullOrEmpty(PlayerName)) PlayerName = "You";
            if (Opponents == null) Opponents = new List<OpponentSetup>();
            if (Opponents.Count == 0)
                Opponents.Add(new OpponentSetup("wren", BotKind.Heuristic));
            if (Seed == 0)
                Seed = (ulong)DateTime.UtcNow.Ticks;
        }
    }
}
