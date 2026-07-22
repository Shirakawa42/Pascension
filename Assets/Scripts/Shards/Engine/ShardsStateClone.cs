using System;
using System.Collections.Generic;
using Pascension.Engine.Core;

namespace Shards.Engine
{
    // Deep copy + extended hashing for search bots (ShardsEngine.Fork). Card DEFS are
    // shared immutable registry data — only the mutable object graph is duplicated.
    // ⚠ Adding a field to ShardsState/ShardsPlayer/ShardsCard? Update DeepCopy, Clone
    // AND ComputeFullHash — the reflection sentinel in ShardsSearchTests fails until
    // the expected field counts there are bumped, which is your reminder.
    public sealed partial class ShardsState
    {
        /// <summary>Deep copy of the mutable state graph. Only valid at engine
        /// quiescence (no parked effect iterators) — enforced by ShardsEngine.Fork,
        /// which is the intended entry point. Rules and card defs stay shared;
        /// aliased cards (PlayedThisTurn ↔ zones) stay aliased in the copy.</summary>
        public ShardsState DeepCopy() => DeepCopy(null);

        /// <summary>With an arena: the SAME routine writing into the arena's recycled
        /// state/cards/map instead of fresh allocations (search forks once per
        /// iteration; the allocation churn was ~20% of self-play CPU). Every field is
        /// overwritten and the lazy card index invalidated, so a recycled clone is
        /// indistinguishable from a fresh one — pinned by Fork_WithArena tests.</summary>
        public ShardsState DeepCopy(ShardsCloneArena arena)
        {
            // Identity map as a flat array — ids come from the NextInstanceId counter,
            // so they are dense in [0, NextInstanceId). A Dictionary here was measurable
            // hashing/allocation overhead at one fork per search iteration.
            ShardsCard[] map;
            ShardsState copy;
            if (arena != null)
            {
                arena.BeginCopy(NextInstanceId);
                map = arena.Map;
                copy = arena.State ??= new ShardsState();
                // The reused state's lazy index still points at the PREVIOUS
                // iteration's card objects (and NextInstanceId alone won't flag it).
                copy.InvalidateCardIndex();
            }
            else
            {
                map = new ShardsCard[NextInstanceId];
                copy = new ShardsState();
            }

            ShardsCard Copy(ShardsCard c)
            {
                if (c == null) return null;
                var dup = map[c.InstanceId];
                if (dup != null) return dup;
                dup = arena != null ? arena.Rent() : new ShardsCard();
                dup.InstanceId = c.InstanceId;
                dup.DefId = c.DefId;
                dup.Owner = c.Owner;
                dup.Zone = c.Zone;
                dup.Exhausted = c.Exhausted;
                dup.FastPlayed = c.FastPlayed;
                dup.DamageThisTurn = c.DamageThisTurn;
                map[c.InstanceId] = dup;
                return dup;
            }

            void CopyList(List<ShardsCard> src, List<ShardsCard> dst)
            {
                dst.Clear();
                if (dst.Capacity < src.Count) dst.Capacity = src.Count;
                foreach (var c in src) dst.Add(Copy(c));
            }

            copy.Rules = Rules; // constants after setup — shared by design
            copy.Dlc = Dlc;
            copy.TurnPlayerIndex = TurnPlayerIndex;
            copy.Round = Round;
            copy.ExtraTurnForPlayer = ExtraTurnForPlayer;
            if (copy.Rng == null) copy.Rng = new DeterministicRng(1);
            copy.Rng.State = Rng.State;
            copy.Rng.Inc = Rng.Inc;
            copy.GameOver = GameOver;
            copy.WinnerIndex = WinnerIndex;
            copy.NextInstanceId = NextInstanceId;
            copy.NextDecisionId = NextDecisionId;

            if (copy.CenterRow == null || copy.CenterRow.Length != CenterRow.Length)
                copy.CenterRow = new ShardsCard[CenterRow.Length];
            for (int s = 0; s < CenterRow.Length; s++)
                copy.CenterRow[s] = Copy(CenterRow[s]);
            CopyList(CenterDeck, copy.CenterDeck);
            CopyList(DestinyRow, copy.DestinyRow);
            CopyList(DestinyDeck, copy.DestinyDeck);
            CopyList(ActiveMonsters, copy.ActiveMonsters);
            CopyList(Banished, copy.Banished);
            copy.PendingMonsterAttacks.Clear();
            copy.PendingMonsterAttacks.AddRange(PendingMonsterAttacks);
            while (copy.Players.Count > Players.Count)
                copy.Players.RemoveAt(copy.Players.Count - 1);
            while (copy.Players.Count < Players.Count)
                copy.Players.Add(new ShardsPlayer());
            for (int i = 0; i < Players.Count; i++)
                Players[i].CloneInto(copy.Players[i], Copy);
            return copy;
        }

        /// <summary>Superset of ComputeHash for fork-determinism checks: additionally
        /// mixes the RNG, id counters, destiny deck, set-asides, turn-scoped counters,
        /// faction play counts and per-card Owner/Zone/FastPlayed. ComputeHash itself
        /// is pinned by wire goldens and must NOT change.</summary>
        public ulong ComputeFullHash()
        {
            unchecked
            {
                ulong h = ComputeHash();
                void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
                void MixCard(ShardsCard c)
                {
                    if (c == null) { Mix(0xF0F0); return; }
                    Mix((ulong)c.InstanceId);
                    Mix((ulong)(c.Owner + 10));
                    Mix((ulong)c.Zone);
                    Mix((ulong)(c.Exhausted ? 3 : 7));
                    Mix((ulong)(c.FastPlayed ? 11 : 13));
                    Mix((ulong)c.DamageThisTurn);
                }

                Mix(Rng.State);
                Mix(Rng.Inc);
                Mix((ulong)NextInstanceId);
                Mix((ulong)NextDecisionId);
                Mix((ulong)(ExtraTurnForPlayer + 10));
                Mix((ulong)(GameOver ? 17 : 19));
                Mix((ulong)(WinnerIndex + 10));
                foreach (var c in CenterDeck) MixCard(c);
                foreach (var c in CenterRow) MixCard(c);
                foreach (var c in DestinyRow) MixCard(c);
                foreach (var c in DestinyDeck) MixCard(c);
                foreach (var c in ActiveMonsters) MixCard(c);
                foreach (var c in Banished) MixCard(c);
                foreach (var p in Players)
                {
                    foreach (char ch in p.CharacterId ?? "") Mix(ch);
                    Mix((ulong)(p.FocusedThisTurn ? 23 : 29));
                    Mix((ulong)(p.ExtraTurnUsed ? 31 : 37));
                    Mix((ulong)(p.IgnoreShieldsThisTurn ? 41 : 43));
                    Mix((ulong)(p.HealthToPowerThisTurn ? 47 : 53));
                    Mix((ulong)(p.CopyHomodeusAlliesThisTurn ? 59 : 61));
                    Mix((ulong)p.NextRecruitsToHand);
                    Mix((ulong)p.NextHomodeusChampionsIntoPlay);
                    Mix((ulong)p.BonusDrawsOnBigHit);
                    Mix((ulong)p.MaxDamageDealtToOneOpponent);
                    foreach (ShardsFaction f in Enum.GetValues(typeof(ShardsFaction)))
                    {
                        Mix((ulong)p.FactionPlays(f));
                        Mix((ulong)p.FactionAllyPlays(f));
                    }
                    foreach (var c in p.PlayedThisTurn) Mix((ulong)c.InstanceId);
                    foreach (var c in p.Deck) MixCard(c);
                    foreach (var c in p.Hand) MixCard(c);
                    foreach (var c in p.Discard) MixCard(c);
                    foreach (var c in p.PlayZone) MixCard(c);
                    foreach (var c in p.Champions) MixCard(c);
                    foreach (var c in p.SetAside) MixCard(c);
                    foreach (var c in p.Destinies) MixCard(c);
                }
                return h;
            }
        }

        /// <summary>Hash of what the given viewer can legitimately SEE: public zones and
        /// scalars, hidden zones as counts only, plus the viewer's own hand. Used by the
        /// search bot's suffix-replay verification — a determinized reconstruction must
        /// match the live engine's public projection.</summary>
        public ulong ComputePublicHash(int viewerIndex)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
                void MixCard(ShardsCard c)
                {
                    if (c == null) { Mix(0xF0F0); return; }
                    Mix((ulong)c.InstanceId);
                    Mix((ulong)(c.Exhausted ? 3 : 7));
                    Mix((ulong)c.DamageThisTurn);
                }

                Mix((ulong)TurnPlayerIndex);
                Mix((ulong)Round);
                Mix((ulong)(ExtraTurnForPlayer + 10));
                Mix((ulong)(GameOver ? 17 : 19));
                Mix((ulong)(WinnerIndex + 10));
                Mix((ulong)CenterDeck.Count);
                Mix((ulong)DestinyDeck.Count);
                foreach (var c in CenterRow) MixCard(c);
                foreach (var c in DestinyRow) MixCard(c);
                foreach (var c in ActiveMonsters) MixCard(c);
                foreach (var c in Banished) MixCard(c);
                foreach (int id in PendingMonsterAttacks) Mix((ulong)id);
                foreach (var p in Players)
                {
                    Mix((ulong)p.Health);
                    Mix((ulong)p.Mastery);
                    Mix((ulong)p.Gems);
                    Mix((ulong)p.Power);
                    Mix((ulong)(p.CharacterExhausted ? 3 : 7));
                    Mix((ulong)(p.Eliminated ? 11 : 13));
                    Mix((ulong)(p.RelicRecruited ? 17 : 19));
                    Mix((ulong)(p.DestinyTaken ? 23 : 29));
                    Mix((ulong)p.Deck.Count);
                    Mix((ulong)p.Hand.Count);
                    foreach (var c in p.Discard) MixCard(c);
                    foreach (var c in p.PlayZone) MixCard(c);
                    foreach (var c in p.Champions) MixCard(c);
                    foreach (var c in p.Destinies) MixCard(c);
                    if (p.Index == viewerIndex)
                        foreach (var c in p.Hand) MixCard(c);
                }
                return h;
            }
        }
    }

    public sealed partial class ShardsPlayer
    {
        /// <summary>Copies every field; cards are resolved through the shared identity
        /// map so aliases (PlayedThisTurn entries living in Discard/PlayZone/Champions)
        /// stay aliases. Zones are copied before PlayedThisTurn on purpose — the map
        /// memoizes, so order only affects which call allocates.</summary>
        public ShardsPlayer Clone(Func<ShardsCard, ShardsCard> copy)
        {
            var dup = new ShardsPlayer();
            CloneInto(dup, copy);
            return dup;
        }

        /// <summary>The single copy routine — also used to overwrite a recycled arena
        /// player, so it must SET every field (never assume defaults).</summary>
        public void CloneInto(ShardsPlayer dup, Func<ShardsCard, ShardsCard> copy)
        {
            dup.Index = Index;
            dup.Name = Name;
            dup.CharacterId = CharacterId;
            dup.FullControl = FullControl;
            dup.Health = Health;
            dup.Mastery = Mastery;
            dup.Gems = Gems;
            dup.Power = Power;
            dup.CharacterExhausted = CharacterExhausted;
            dup.FocusedThisTurn = FocusedThisTurn;
            dup.RelicRecruited = RelicRecruited;
            dup.DestinyTaken = DestinyTaken;
            dup.ExtraTurnUsed = ExtraTurnUsed;
            dup.Eliminated = Eliminated;
            dup.IgnoreShieldsThisTurn = IgnoreShieldsThisTurn;
            dup.HealthToPowerThisTurn = HealthToPowerThisTurn;
            dup.NextRecruitsToHand = NextRecruitsToHand;
            dup.NextHomodeusChampionsIntoPlay = NextHomodeusChampionsIntoPlay;
            dup.CopyHomodeusAlliesThisTurn = CopyHomodeusAlliesThisTurn;
            dup.BonusDrawsOnBigHit = BonusDrawsOnBigHit;
            dup.MaxDamageDealtToOneOpponent = MaxDamageDealtToOneOpponent;

            void CopyList(List<ShardsCard> src, List<ShardsCard> dst)
            {
                dst.Clear();
                if (dst.Capacity < src.Count) dst.Capacity = src.Count;
                foreach (var c in src) dst.Add(copy(c));
            }

            CopyList(Deck, dup.Deck);
            CopyList(Hand, dup.Hand);
            CopyList(Discard, dup.Discard);
            CopyList(PlayZone, dup.PlayZone);
            CopyList(Champions, dup.Champions);
            CopyList(SetAside, dup.SetAside);
            CopyList(Destinies, dup.Destinies);
            CopyList(PlayedThisTurn, dup.PlayedThisTurn);
            dup._factionPlays.Clear();
            foreach (var kv in _factionPlays) dup._factionPlays[kv.Key] = kv.Value;
            dup._factionAllyPlays.Clear();
            foreach (var kv in _factionAllyPlays) dup._factionAllyPlays[kv.Key] = kv.Value;
        }
    }
}
