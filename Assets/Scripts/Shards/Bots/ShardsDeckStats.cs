using System.Collections.Generic;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Derived per-deck statistics — the "points" a player reads off a position:
    /// how many gems, how much damage, how much mastery and how many cards this deck
    /// actually produces per turn, plus its faction composition and which conditional
    /// keywords are live.
    ///
    /// WHY THIS IS A SEPARATE, ANALYTIC LAYER. The previous neural evaluator was handed
    /// summed bags of card vectors and asked to learn a value function built from RATIOS —
    /// per-turn output is `D x Sum(stat)/N`, the ascend clock is `(30-mastery)/masteryPerTurn`,
    /// the kill clock is `health/damagePerTurn`. A network that never sees N multiplicatively
    /// against the sum cannot compute a division, and it measured 40.6% against having no
    /// evaluator at all. So every ratio is computed exactly here and handed over, rather
    /// than left to be discovered.
    ///
    /// Everything is UNWEIGHTED and tuning-free: these are counts and rates, not values.
    /// The evaluator applies judgement on top; this layer only reports what the deck does.</summary>
    public sealed class ShardsDeckStats
    {
        // ---- size and throughput ----
        /// <summary>Cards in the owned cycle: deck + hand + discard + play zone. Champions,
        /// destinies and set-aside relics are NOT in N — they act every turn from the board
        /// rather than being drawn, which is why a champion is worth several times the same
        /// effect printed on a card.</summary>
        public int N;
        /// <summary>Cards actually SEEN per turn: the hand size inflated by the deck's own
        /// draw rate, since drawn cards can themselves draw. Diverges fast — a cantrip-heavy
        /// deck sees far more than 5. Capped, because the fixed point runs away as draws
        /// approach N.</summary>
        public double D;
        /// <summary>Turns to cycle the whole deck once: N / D. The Infinity Shard wait and
        /// the value of thinning both scale off this.</summary>
        public double CycleTurns;

        // ---- per-turn rates (what the deck produces, on average, each turn) ----
        public double GemsPerTurn, PowerPerTurn, MasteryPerTurn, HealthPerTurn, DrawsPerTurn;
        /// <summary>Per-turn output from the BOARD — champion and destiny exhausts. Not
        /// divided by N: these fire every turn regardless of what is drawn.</summary>
        public double BoardGems, BoardPower, BoardMastery;

        // ---- composition ----
        /// <summary>Owned card count per faction, across deck+hand+discard+play+champions —
        /// the pool Allegiance counts over. Faction.None (Crystal, Blaster, Shard Reactor,
        /// Infinity Shard) is counted but satisfies no faction condition.</summary>
        public readonly Dictionary<ShardsFaction, int> FactionCounts = new();
        /// <summary>Share of the owned pool per faction, 0..1.</summary>
        public readonly Dictionary<ShardsFaction, double> FactionShare = new();
        /// <summary>Distinct factions owned, ignoring None — the quantity Duel's reworked
        /// Dominion (3+ distinct factions played) draws on.</summary>
        public int DistinctFactions;

        // ---- conditional liveness ----
        /// <summary>Rough per-turn probability that another Undergrowth ALLY is available to
        /// satisfy Unify — champions never satisfy it. This is exactly why a Unify card is
        /// worth more the more Undergrowth you own: liveness, not a synergy bonus.</summary>
        public double UnifyLiveness;
        /// <summary>Factions where the owned count already meets Allegiance 4. The card
        /// itself counts toward its own Allegiance, so a 4th copy is a threshold, not a
        /// gradient.</summary>
        public int FactionsAtAllegiance4;
        /// <summary>Wraethe cards in the discard — Echo's condition, usually trivially true
        /// once a Wraethe card has been played.</summary>
        public int WraetheInDiscard;

        // ---- clocks input ----
        public int Health, Mastery, Champions;
        /// <summary>True when the Infinity Shard has been banished — the ascend route is then
        /// DEAD, discontinuously, and several effects can banish from your own discard.</summary>
        public bool ShardBanished;

        private const int MaxDrawsSeen = 16;

        public static ShardsDeckStats For(ShardsState state, int playerIndex)
        {
            var player = state.Players[playerIndex];
            var s = new ShardsDeckStats
            {
                Health = player.Health,
                Mastery = player.Mastery,
                Champions = player.Champions.Count
            };
            int bucket = CardStatics.BucketOf(player.Mastery);

            double gems = 0, power = 0, mastery = 0, health = 0, draws = 0;
            int undergrowthAllies = 0;

            void Count(ShardsCard card, bool inCycle)
            {
                var def = card.Def;
                var faction = def.Faction;
                s.FactionCounts[faction] = s.FactionCounts.TryGetValue(faction, out int c) ? c + 1 : 1;
                if (!inCycle) return;
                s.N++;
                var atoms = ShardsCardStatics.Get(def).Play[bucket];
                // Unconditional gains only. A conditional gain is priced by its liveness,
                // which is what UnifyLiveness and the Allegiance/Echo counts below express —
                // folding it in here would double-count the condition.
                gems += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Gems];
                power += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Power];
                mastery += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Mastery];
                health += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Health];
                draws += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Draw];
                if (faction == ShardsFaction.Undergrowth && !def.IsChampion) undergrowthAllies++;
            }

            foreach (var c in player.Deck) Count(c, true);
            foreach (var c in player.Hand) Count(c, true);
            foreach (var c in player.PlayZone) Count(c, true);
            foreach (var c in player.Discard)
            {
                Count(c, true);
                if (c.Def.Faction == ShardsFaction.Wraethe) s.WraetheInDiscard++;
            }
            // Board permanents are OUTSIDE the cycle — they contribute per turn, not per draw.
            foreach (var c in player.Champions) Count(c, false);
            foreach (var c in player.Destinies) Count(c, false);

            foreach (var c in player.Champions) AddBoard(s, c, bucket);
            foreach (var c in player.Destinies) AddBoard(s, c, bucket);

            s.ShardBanished = true;
            foreach (var c in player.Deck) if (c.DefId == "infinity_shard") s.ShardBanished = false;
            foreach (var c in player.Hand) if (c.DefId == "infinity_shard") s.ShardBanished = false;
            foreach (var c in player.Discard) if (c.DefId == "infinity_shard") s.ShardBanished = false;
            foreach (var c in player.PlayZone) if (c.DefId == "infinity_shard") s.ShardBanished = false;

            if (s.N == 0) return s;

            // Cards seen per turn. Each drawn card draws `draws/N` more on average, so the
            // series sums to handSize / (1 - drawRate). Clamped: as the draw rate approaches
            // 1 the fixed point diverges, and no real deck cycles itself infinitely.
            double handSize = state.Rules.HandSize;
            double drawRate = draws / s.N;
            s.D = System.Math.Min(MaxDrawsSeen, handSize / System.Math.Max(0.30, 1.0 - drawRate));
            s.CycleTurns = s.N / System.Math.Max(0.01, s.D);

            double perTurn = s.D / s.N;
            s.GemsPerTurn = gems * perTurn + s.BoardGems;
            s.PowerPerTurn = power * perTurn + s.BoardPower;
            s.MasteryPerTurn = mastery * perTurn + s.BoardMastery;
            s.HealthPerTurn = health * perTurn;
            s.DrawsPerTurn = draws * perTurn;

            int pool = 0;
            foreach (var kv in s.FactionCounts) pool += kv.Value;
            foreach (var kv in s.FactionCounts)
            {
                s.FactionShare[kv.Key] = pool == 0 ? 0 : (double)kv.Value / pool;
                if (kv.Key == ShardsFaction.None) continue;
                s.DistinctFactions++;
                if (kv.Value >= 4) s.FactionsAtAllegiance4++;
            }

            // Unify needs ANOTHER Undergrowth ally in the same turn. With u such allies in a
            // cycle of N and D cards seen, the chance at least one more shows up alongside a
            // given card is ~1-(1-u/N)^(D-1). Approximate, and deliberately so: the point is
            // that liveness RISES with Undergrowth share, which is what makes a Unify card
            // worth more in an Undergrowth deck.
            double u = undergrowthAllies / (double)s.N;
            s.UnifyLiveness = undergrowthAllies <= 1
                ? 0
                : 1.0 - System.Math.Pow(1.0 - u, System.Math.Max(0, s.D - 1));
            return s;
        }

        private static void AddBoard(ShardsDeckStats s, ShardsCard card, int bucket)
        {
            var atoms = ShardsCardStatics.Get(card.Def).Exhaust[bucket];
            s.BoardGems += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Gems];
            s.BoardPower += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Power];
            s.BoardMastery += atoms.Gains[EffectAtoms.Unconditional, EffectAtoms.Mastery];
        }
    }
}
