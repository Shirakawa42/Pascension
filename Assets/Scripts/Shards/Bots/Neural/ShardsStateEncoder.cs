using System;
using System.Collections.Generic;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>
    /// Fixed 768-float encoding of a SoI position from one seat's viewpoint — the
    /// input schema shared VERBATIM by self-play data generation, training (numpy
    /// reads the binary records) and in-search inference. Two invariants:
    ///
    /// 1. INFORMATION-SET ENCODING: exactly what ShardsDeterminizer preserves — own
    ///    zones exact, opponent hand∪deck as ONE pooled composition + hand count.
    ///    Training positions (true states) and search leaves (determinized clones)
    ///    therefore share a single feature distribution: no train/inference drift.
    /// 2. DLC-PROOF: cards contribute property vectors derived from ShardsCardStatics
    ///    atoms — no def-id identity anywhere. New cards encode automatically (and
    ///    unannotated Custom effects still trip the annotation guard test first).
    ///
    /// Layout (see consts): 14 zone pools × 52-dim card vectors = 728, then champion/
    /// monster dynamics (7), per-player scalars (2×12), global scalars (9) = 768.
    /// Bump SchemaVersion on ANY layout change — selfplay files and weight blobs
    /// carry it and refuse to mix.
    /// </summary>
    public static class ShardsStateEncoder
    {
        public const int SchemaVersion = 1;
        public const int CardVecSize = 52;
        public const int PoolCount = 14;
        public const int FeatureCount = PoolCount * CardVecSize + 7 + 2 * 12 + 9; // 768

        // Pool order (offsets = index × CardVecSize). Viewer-relative.
        private const int OwnHand = 0, OwnDeck = 1, OwnDiscard = 2, OwnPlayZone = 3,
            OwnChampions = 4, OwnSetPieces = 5, OppPool = 6, OppDiscard = 7,
            OppPlayZone = 8, OppChampions = 9, OppSetPieces = 10,
            CenterRow = 11, DestinyRow = 12, Monsters = 13;

        private const int DynamicsOffset = PoolCount * CardVecSize;      // 728
        private const int PlayerScalarsOffset = DynamicsOffset + 7;      // 735
        private const int GlobalScalarsOffset = PlayerScalarsOffset + 24; // 759

        // ---- per-(def, masteryBucket) property vectors, cached like ShardsValueModel ----

        private static readonly Dictionary<ShardsCardDef, float[][]> CardVecCache = new();
        private static readonly object CacheLock = new();

        private static float[] CardVec(ShardsCardDef def, int mastery)
        {
            int bucket = CardStatics.BucketOf(mastery);
            float[][] perBucket;
            lock (CacheLock)
            {
                if (!CardVecCache.TryGetValue(def, out perBucket))
                {
                    perBucket = BuildCardVecs(def);
                    CardVecCache[def] = perBucket;
                }
            }
            return perBucket[bucket];
        }

        private static float[][] BuildCardVecs(ShardsCardDef def)
        {
            var statics = ShardsCardStatics.Get(def);
            var result = new float[CardStatics.Buckets][];
            for (int b = 0; b < CardStatics.Buckets; b++)
            {
                var v = new float[CardVecSize];
                int i = 0;
                var play = statics.Play[b];
                for (int r = 0; r < 5; r++) v[i++] = (float)play.Gains[EffectAtoms.Unconditional, r];
                for (int r = 0; r < 5; r++)
                    v[i++] = (float)(play.Gains[EffectAtoms.UnifyClass, r] + play.Gains[EffectAtoms.DominionClass, r] +
                                     play.Gains[EffectAtoms.IfClass, r] + play.Gains[EffectAtoms.FactionClass, r]);
                for (int r = 0; r < 5; r++) v[i++] = (float)play.PerUnit[r];
                var exhaust = statics.Exhaust[b];
                for (int r = 0; r < 5; r++)
                    v[i++] = (float)(exhaust.Gains[EffectAtoms.Unconditional, r] + exhaust.Gains[EffectAtoms.UnifyClass, r] +
                                     exhaust.Gains[EffectAtoms.DominionClass, r] + exhaust.Gains[EffectAtoms.IfClass, r] +
                                     exhaust.Gains[EffectAtoms.FactionClass, r] + exhaust.PerUnit[r]) -
                            (r == 0 ? def.ExhaustGemCost : 0);
                var reward = statics.Reward[b];
                for (int r = 0; r < 5; r++)
                    v[i++] = (float)(reward.Gains[EffectAtoms.Unconditional, r] + reward.PerUnit[r]);
                // structural (9)
                v[i++] = play.Warps * Math.Min(play.WarpMaxCost, 8) / 8f;
                v[i++] = play.RecruitsRow * Math.Min(play.RecruitMaxCost, 8) / 8f;
                v[i++] = play.DestroysChampions;
                v[i++] = play.BanishCapacity / 3f;
                v[i++] = play.ReturnsFromDiscard ? 1 : 0;
                v[i++] = play.CopyEffects;
                v[i++] = play.OppMasteryLoss / 3f;
                v[i++] = play.AllLoseHealth / 5f;
                v[i++] = play.AllLoseMastery / 3f;
                // printed scalars (4)
                v[i++] = def.Cost / 8f;
                v[i++] = def.Defense / 9f;
                v[i++] = def.Shield / 3f;
                v[i++] = def.Taunt ? 1 : 0;
                // faction one-hot (7) + type one-hot (7), enum-order indexed
                int faction = (int)def.Faction;
                if (faction >= 0 && faction < 7) v[i + faction] = 1;
                i += 7;
                int type = (int)def.Type;
                if (type >= 0 && type < 7) v[i + type] = 1;
                i += 7;
                if (i != CardVecSize)
                    throw new InvalidOperationException($"CardVec layout drifted: {i} != {CardVecSize}");
                result[b] = v;
            }
            return result;
        }

        // ------------------------------------------------------------------ encode

        /// <summary>Fills dst (length ≥ FeatureCount) with the viewer's encoding.</summary>
        public static void Encode(ShardsState state, int viewerIndex, float[] dst)
        {
            Array.Clear(dst, 0, FeatureCount);
            var me = state.Players[viewerIndex];
            var opp = state.Players[1 - viewerIndex];

            PoolInto(dst, OwnHand, me.Hand, me.Mastery);
            PoolInto(dst, OwnDeck, me.Deck, me.Mastery);
            PoolInto(dst, OwnDiscard, me.Discard, me.Mastery);
            PoolInto(dst, OwnPlayZone, me.PlayZone, me.Mastery);
            PoolInto(dst, OwnChampions, me.Champions, me.Mastery);
            PoolInto(dst, OwnSetPieces, me.Destinies, me.Mastery);
            PoolInto(dst, OwnSetPieces, me.SetAside, me.Mastery);
            // Opponent hand∪deck as ONE pool — the information-set boundary.
            PoolInto(dst, OppPool, opp.Hand, opp.Mastery);
            PoolInto(dst, OppPool, opp.Deck, opp.Mastery);
            PoolInto(dst, OppDiscard, opp.Discard, opp.Mastery);
            PoolInto(dst, OppPlayZone, opp.PlayZone, opp.Mastery);
            PoolInto(dst, OppChampions, opp.Champions, opp.Mastery);
            PoolInto(dst, OppSetPieces, opp.Destinies, opp.Mastery);
            PoolInto(dst, OppSetPieces, opp.SetAside, opp.Mastery);
            foreach (var card in state.CenterRow)
                if (card != null)
                    AddCardVec(dst, CenterRow, card.Def, me.Mastery);
            PoolInto(dst, DestinyRow, state.DestinyRow, me.Mastery);
            PoolInto(dst, Monsters, state.ActiveMonsters, me.Mastery);

            // Champion/monster dynamics (7).
            int d = DynamicsOffset;
            dst[d + 0] = RemainingDefense(me.Champions) / 12f;
            dst[d + 1] = Exhausted(me.Champions) / 4f;
            dst[d + 2] = TauntCount(me.Champions);
            dst[d + 3] = RemainingDefense(opp.Champions) / 12f;
            dst[d + 4] = Exhausted(opp.Champions) / 4f;
            dst[d + 5] = TauntCount(opp.Champions);
            dst[d + 6] = RemainingDefense(state.ActiveMonsters) / 10f;

            PlayerScalars(dst, PlayerScalarsOffset, me);
            PlayerScalars(dst, PlayerScalarsOffset + 12, opp);

            int g = GlobalScalarsOffset;
            dst[g + 0] = state.Round / 20f;
            dst[g + 1] = state.TurnPlayerIndex == viewerIndex ? 1 : 0;
            dst[g + 2] = viewerIndex == 0 ? 1 : 0;
            dst[g + 3] = (state.Dlc & ShardsDlc.RelicsOfTheFuture) != 0 ? 1 : 0;
            dst[g + 4] = (state.Dlc & ShardsDlc.ShadowOfSalvation) != 0 ? 1 : 0;
            dst[g + 5] = (state.Dlc & ShardsDlc.IntoTheHorizon) != 0 ? 1 : 0;
            dst[g + 6] = state.CenterDeck.Count / 88f;
            dst[g + 7] = state.DestinyDeck.Count / 24f;
            dst[g + 8] = state.PendingMonsterAttacks.Count;
        }

        private static void PlayerScalars(float[] dst, int offset, ShardsPlayer p)
        {
            dst[offset + 0] = p.Health / 50f;
            dst[offset + 1] = p.Mastery / 30f;
            dst[offset + 2] = p.Gems / 10f;
            dst[offset + 3] = p.Power / 15f;
            dst[offset + 4] = p.Hand.Count / 5f;
            dst[offset + 5] = p.Deck.Count / 20f;
            dst[offset + 6] = p.Discard.Count / 20f;
            dst[offset + 7] = p.CharacterExhausted ? 1 : 0;
            dst[offset + 8] = p.FocusedThisTurn ? 1 : 0;
            dst[offset + 9] = p.RelicRecruited ? 1 : 0;
            dst[offset + 10] = p.DestinyTaken ? 1 : 0;
            dst[offset + 11] = p.ExtraTurnUsed ? 1 : 0;
        }

        private static void PoolInto(float[] dst, int pool, List<ShardsCard> cards, int mastery)
        {
            foreach (var card in cards)
                AddCardVec(dst, pool, card.Def, mastery);
        }

        private static void AddCardVec(float[] dst, int pool, ShardsCardDef def, int mastery)
        {
            var v = CardVec(def, mastery);
            int offset = pool * CardVecSize;
            for (int i = 0; i < CardVecSize; i++)
                dst[offset + i] += v[i];
        }

        private static float RemainingDefense(List<ShardsCard> champions)
        {
            float sum = 0;
            foreach (var c in champions)
                sum += Math.Max(0, c.Def.Defense - c.DamageThisTurn);
            return sum;
        }

        private static int Exhausted(List<ShardsCard> champions)
        {
            int n = 0;
            foreach (var c in champions)
                if (c.Exhausted)
                    n++;
            return n;
        }

        private static int TauntCount(List<ShardsCard> champions)
        {
            int n = 0;
            foreach (var c in champions)
                if (c.Def.Taunt)
                    n++;
            return n;
        }
    }
}
