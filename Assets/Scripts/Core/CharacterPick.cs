using System.Collections.Generic;
using Pascension.Engine.Core;

namespace Pascension.Core
{
    /// <summary>
    /// The "random character" sentinel shared by both games' setup screens, plus the
    /// deterministic resolver that turns sentinel picks into concrete, distinct ids at
    /// match start. Pure logic — resolution MUST happen before any game config is
    /// built (the sentinel is never a legal engine id).
    /// </summary>
    public static class CharacterPick
    {
        /// <summary>Reserved pick id meaning "assign me a random character at start".</summary>
        public const string RandomId = "random";

        public static bool IsRandom(string id) => id == RandomId;

        /// <summary>The no-duplicate rule: true when another pick already holds this
        /// concrete id (sentinel picks never collide). <paramref name="self"/> is the
        /// picker's own index (-1 when it has no slot in the list).</summary>
        public static bool IsTakenByOther(IReadOnlyList<string> picks, int self, string id)
        {
            if (IsRandom(id) || string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < picks.Count; i++)
                if (i != self && picks[i] == id)
                    return true;
            return false;
        }

        /// <summary>Resolve sentinel picks to concrete ids distinct from every other
        /// pick, drawing uniformly (seeded — same seed, same heroes) from the unused
        /// roster. Concrete picks pass through untouched. Should randoms ever outnumber
        /// the unused roster (both games ship roster ≥ max players, so defensive only)
        /// the tail reuses the roster round-robin instead of failing.</summary>
        public static List<string> ResolveRandoms(IReadOnlyList<string> picks,
            IReadOnlyList<string> roster, ulong seed)
        {
            var pool = new List<string>(roster.Count);
            foreach (var id in roster)
            {
                bool taken = false;
                for (int i = 0; i < picks.Count && !taken; i++)
                    taken = picks[i] == id;
                if (!taken) pool.Add(id);
            }

            // Own RNG stream (sequence 97): never touches engine randomness, and stays
            // uncorrelated with the engine's default-sequence rolls on the same seed.
            var rng = new DeterministicRng(seed, sequence: 97UL);
            var resolved = new List<string>(picks.Count);
            int wrap = 0;
            foreach (var pick in picks)
            {
                if (!IsRandom(pick))
                {
                    resolved.Add(pick);
                }
                else if (pool.Count > 0)
                {
                    int at = rng.Next(pool.Count);
                    resolved.Add(pool[at]);
                    pool.RemoveAt(at);
                }
                else
                {
                    resolved.Add(roster[wrap++ % roster.Count]);
                }
            }
            return resolved;
        }
    }
}
