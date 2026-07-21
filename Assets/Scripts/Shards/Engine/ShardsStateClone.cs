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
        public ShardsState DeepCopy()
        {
            var map = new Dictionary<int, ShardsCard>(256);
            ShardsCard Copy(ShardsCard c)
            {
                if (c == null) return null;
                if (map.TryGetValue(c.InstanceId, out var dup)) return dup;
                dup = new ShardsCard
                {
                    InstanceId = c.InstanceId,
                    DefId = c.DefId,
                    Owner = c.Owner,
                    Zone = c.Zone,
                    Exhausted = c.Exhausted,
                    FastPlayed = c.FastPlayed,
                    DamageThisTurn = c.DamageThisTurn
                };
                map[c.InstanceId] = dup;
                return dup;
            }

            var copy = new ShardsState
            {
                Rules = Rules, // constants after setup — shared by design
                Dlc = Dlc,
                TurnPlayerIndex = TurnPlayerIndex,
                Round = Round,
                ExtraTurnForPlayer = ExtraTurnForPlayer,
                Rng = new DeterministicRng(1) { State = Rng.State, Inc = Rng.Inc },
                GameOver = GameOver,
                WinnerIndex = WinnerIndex,
                NextInstanceId = NextInstanceId,
                NextDecisionId = NextDecisionId,
                CenterRow = new ShardsCard[CenterRow.Length]
            };
            for (int s = 0; s < CenterRow.Length; s++)
                copy.CenterRow[s] = Copy(CenterRow[s]);
            foreach (var c in CenterDeck) copy.CenterDeck.Add(Copy(c));
            foreach (var c in DestinyRow) copy.DestinyRow.Add(Copy(c));
            foreach (var c in DestinyDeck) copy.DestinyDeck.Add(Copy(c));
            foreach (var c in ActiveMonsters) copy.ActiveMonsters.Add(Copy(c));
            foreach (var c in Banished) copy.Banished.Add(Copy(c));
            copy.PendingMonsterAttacks.AddRange(PendingMonsterAttacks);
            foreach (var p in Players)
                copy.Players.Add(p.Clone(Copy));
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
            var dup = new ShardsPlayer
            {
                Index = Index,
                Name = Name,
                CharacterId = CharacterId,
                FullControl = FullControl,
                Health = Health,
                Mastery = Mastery,
                Gems = Gems,
                Power = Power,
                CharacterExhausted = CharacterExhausted,
                FocusedThisTurn = FocusedThisTurn,
                RelicRecruited = RelicRecruited,
                DestinyTaken = DestinyTaken,
                ExtraTurnUsed = ExtraTurnUsed,
                Eliminated = Eliminated,
                IgnoreShieldsThisTurn = IgnoreShieldsThisTurn,
                HealthToPowerThisTurn = HealthToPowerThisTurn,
                NextRecruitsToHand = NextRecruitsToHand,
                NextHomodeusChampionsIntoPlay = NextHomodeusChampionsIntoPlay,
                CopyHomodeusAlliesThisTurn = CopyHomodeusAlliesThisTurn,
                BonusDrawsOnBigHit = BonusDrawsOnBigHit,
                MaxDamageDealtToOneOpponent = MaxDamageDealtToOneOpponent
            };
            foreach (var c in Deck) dup.Deck.Add(copy(c));
            foreach (var c in Hand) dup.Hand.Add(copy(c));
            foreach (var c in Discard) dup.Discard.Add(copy(c));
            foreach (var c in PlayZone) dup.PlayZone.Add(copy(c));
            foreach (var c in Champions) dup.Champions.Add(copy(c));
            foreach (var c in SetAside) dup.SetAside.Add(copy(c));
            foreach (var c in Destinies) dup.Destinies.Add(copy(c));
            foreach (var c in PlayedThisTurn) dup.PlayedThisTurn.Add(copy(c));
            foreach (var kv in _factionPlays) dup._factionPlays[kv.Key] = kv.Value;
            foreach (var kv in _factionAllyPlays) dup._factionAllyPlays[kv.Key] = kv.Value;
            return dup;
        }
    }
}
