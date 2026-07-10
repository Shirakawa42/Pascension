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
        public string DestinyId;
        public bool Eliminated;

        /// <summary>Faction play counts this turn (fast-played mercenaries included).</summary>
        private readonly Dictionary<ShardsFaction, int> _factionPlays = new();

        public int FactionPlays(ShardsFaction faction) =>
            _factionPlays.TryGetValue(faction, out int n) ? n : 0;

        public void CountFactionPlay(ShardsFaction faction)
        {
            _factionPlays[faction] = FactionPlays(faction) + 1;
        }

        public void ResetTurn()
        {
            Gems = 0;
            Power = 0;
            FocusedThisTurn = false;
            _factionPlays.Clear();
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
                foreach (var list in new[] { p.Deck, p.Hand, p.Discard, p.PlayZone, p.Champions, p.SetAside })
                    foreach (var card in list)
                        if (card.InstanceId == instanceId)
                            return card;
            }
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
                }

                Mix((ulong)TurnPlayerIndex);
                Mix((ulong)Round);
                Mix((ulong)Dlc);
                foreach (var card in CenterRow) MixCard(card);
                foreach (var card in CenterDeck) MixCard(card);
                foreach (var p in Players)
                {
                    Mix((ulong)p.Health);
                    Mix((ulong)p.Mastery);
                    Mix((ulong)p.Gems);
                    Mix((ulong)p.Power);
                    Mix((ulong)(p.CharacterExhausted ? 1 : 0));
                    Mix((ulong)(p.Eliminated ? 1 : 0));
                    foreach (var list in new[] { p.Deck, p.Hand, p.Discard, p.PlayZone, p.Champions })
                        foreach (var card in list)
                            MixCard(card);
                }
                return h;
            }
        }
    }
}
