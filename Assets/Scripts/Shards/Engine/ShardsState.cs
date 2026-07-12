using System.Collections.Generic;
using Pascension.Engine.Core;

namespace Shards.Engine
{
    public sealed class ShardsPlayer
    {
        public int Index;
        public string Name;
        public string CharacterId;
        public bool FullControl;

        public int Health;
        public int Mastery;
        public int Gems;
        public int Power;

        /// <summary>Character card exhausted (Focus or a character exhaust power).</summary>
        public bool CharacterExhausted;
        public bool FocusedThisTurn;

        public List<ShardsCard> Deck = new();
        public List<ShardsCard> Hand = new();
        public List<ShardsCard> Discard = new();
        /// <summary>Allies/mercenaries played this turn — visible until cleanup.</summary>
        public List<ShardsCard> PlayZone = new();
        /// <summary>Champions stay in play indefinitely; any count.</summary>
        public List<ShardsCard> Champions = new();
        /// <summary>Relics (DLC1) / Destinies (DLC3) waiting to be earned.</summary>
        public List<ShardsCard> SetAside = new();

        public bool RelicRecruited;
        /// <summary>Taken destinies (normally one; Agony/Malice rewards can add more).</summary>
        public List<ShardsCard> Destinies = new();
        /// <summary>The once-per-game M5+ destiny pick was used.</summary>
        public bool DestinyTaken;
        /// <summary>Slipstream Shard's M20 extra turn is once per game.</summary>
        public bool ExtraTurnUsed;
        public bool Eliminated;

        // ---- turn-scoped state (cleared in ResetTurn) ----

        /// <summary>Faction play counts this turn, any card type (fast-plays included).</summary>
        private readonly Dictionary<ShardsFaction, int> _factionPlays = new();
        /// <summary>Faction ALLY play counts (Unify cares about allies specifically —
        /// champions never satisfy it; mercenaries and starters are allies).</summary>
        private readonly Dictionary<ShardsFaction, int> _factionAllyPlays = new();
        /// <summary>Everything played this turn incl. fast-plays/warps and champions.</summary>
        public readonly List<ShardsCard> PlayedThisTurn = new();

        public bool IgnoreShieldsThisTurn;        // Ru Bo Vai M10
        public bool HealthToPowerThisTurn;        // Entropic Talons conversion
        /// <summary>Counters, not flags — two Numeri Drones (or an Ojas-copied Anomaly
        /// Cleric) can arm several redirects in one turn.</summary>
        public int NextRecruitsToHand;            // Anomaly Cleric M10
        public int NextHomodeusChampionsIntoPlay; // Numeri Drones
        public bool CopyHomodeusAlliesThisTurn;   // General Decurion M20
        /// <summary>Heart of Nothing: extra end-of-turn draws if 10+ unprevented damage
        /// landed on a single opponent this turn.</summary>
        public int BonusDrawsOnBigHit;
        /// <summary>Largest unprevented damage total dealt to one opponent this turn.</summary>
        public int MaxDamageDealtToOneOpponent;

        public int FactionPlays(ShardsFaction faction) =>
            _factionPlays.TryGetValue(faction, out int n) ? n : 0;

        public int FactionAllyPlays(ShardsFaction faction) =>
            _factionAllyPlays.TryGetValue(faction, out int n) ? n : 0;

        public void CountFactionPlay(ShardsFaction faction, bool isAlly)
        {
            _factionPlays[faction] = FactionPlays(faction) + 1;
            if (isAlly)
                _factionAllyPlays[faction] = FactionAllyPlays(faction) + 1;
        }

        public void ResetTurn()
        {
            Gems = 0;
            Power = 0;
            FocusedThisTurn = false;
            _factionPlays.Clear();
            _factionAllyPlays.Clear();
            PlayedThisTurn.Clear();
            IgnoreShieldsThisTurn = false;
            HealthToPowerThisTurn = false;
            NextRecruitsToHand = 0;
            NextHomodeusChampionsIntoPlay = 0;
            CopyHomodeusAlliesThisTurn = false;
            BonusDrawsOnBigHit = 0;
            MaxDamageDealtToOneOpponent = 0;
        }
    }

    public sealed class ShardsState
    {
        public ShardsRules Rules = new();
        public ShardsDlc Dlc;
        public List<ShardsPlayer> Players = new();
        public int TurnPlayerIndex;
        public int Round = 1;

        public List<ShardsCard> CenterDeck = new();
        public ShardsCard[] CenterRow;
        /// <summary>Shared face-up destiny row (ItH) — shrinks, never refills.</summary>
        public List<ShardsCard> DestinyRow = new();
        /// <summary>Revealed Ingeminex beside the row (any number).</summary>
        public List<ShardsCard> ActiveMonsters = new();
        /// <summary>Shared removed-from-game pile (banish).</summary>
        public List<ShardsCard> Banished = new();
        /// <summary>Ingeminex revealed this turn — each attacks ALL players once at end of turn.</summary>
        public List<int> PendingMonsterAttacks = new();
        /// <summary>Undealt destinies (Stolen Futures adds from here to the row).</summary>
        public List<ShardsCard> DestinyDeck = new();
        /// <summary>Slipstream Shard: this player takes another turn after the current one.</summary>
        public int ExtraTurnForPlayer = -1;

        public DeterministicRng Rng;
        public bool GameOver;
        public int WinnerIndex = -1;

        public int NextInstanceId = 1;
        public int NextDecisionId = 1;

        public ShardsPlayer TurnPlayer => Players[TurnPlayerIndex];

        public IEnumerable<ShardsPlayer> LivingOpponentsOf(int playerIndex)
        {
            foreach (var p in Players)
                if (p.Index != playerIndex && !p.Eliminated)
                    yield return p;
        }

        public int LivingCount
        {
            get
            {
                int n = 0;
                foreach (var p in Players)
                    if (!p.Eliminated)
                        n++;
                return n;
            }
        }

        public ShardsCard FindCard(int instanceId)
        {
            foreach (var card in CenterRow)
                if (card != null && card.InstanceId == instanceId)
                    return card;
            foreach (var card in CenterDeck)
                if (card.InstanceId == instanceId)
                    return card;
            foreach (var p in Players)
            {
                foreach (var list in new[] { p.Deck, p.Hand, p.Discard, p.PlayZone, p.Champions, p.SetAside, p.Destinies })
                    foreach (var card in list)
                        if (card.InstanceId == instanceId)
                            return card;
            }
            foreach (var list in new[] { DestinyRow, ActiveMonsters, Banished })
                foreach (var card in list)
                    if (card.InstanceId == instanceId)
                        return card;
            return null;
        }

        /// <summary>FNV-1a state hash for determinism/replay checks.</summary>
        public ulong ComputeHash()
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
                void MixCard(ShardsCard c)
                {
                    if (c == null) { Mix(0xDEAD); return; }
                    Mix((ulong)c.InstanceId);
                    foreach (char ch in c.DefId) Mix(ch);
                    Mix((ulong)(c.Exhausted ? 1 : 0));
                    Mix((ulong)c.DamageThisTurn);
                }

                Mix((ulong)TurnPlayerIndex);
                Mix((ulong)Round);
                Mix((ulong)Dlc);
                foreach (var card in CenterRow) MixCard(card);
                foreach (var card in CenterDeck) MixCard(card);
                foreach (var card in DestinyRow) MixCard(card);
                foreach (var card in ActiveMonsters) MixCard(card);
                foreach (var card in Banished) MixCard(card);
                foreach (int id in PendingMonsterAttacks) Mix((ulong)id);
                foreach (var p in Players)
                {
                    Mix((ulong)p.Health);
                    Mix((ulong)p.Mastery);
                    Mix((ulong)p.Gems);
                    Mix((ulong)p.Power);
                    Mix((ulong)(p.CharacterExhausted ? 1 : 0));
                    Mix((ulong)(p.Eliminated ? 1 : 0));
                    Mix((ulong)(p.RelicRecruited ? 1 : 0));
                    Mix((ulong)(p.DestinyTaken ? 1 : 0));
                    foreach (var list in new[] { p.Deck, p.Hand, p.Discard, p.PlayZone, p.Champions, p.Destinies })
                        foreach (var card in list)
                            MixCard(card);
                }
                return h;
            }
        }
    }
}
