using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Effects;
using Pascension.Engine.Stack;

namespace Pascension.Engine.Core
{
    /// <summary>Root mutable game state. Mutated only by the engine (GameApi); read anywhere.</summary>
    public sealed class GameState
    {
        public GameRules Rules = new();
        public List<PlayerState> Players = new();
        public Market Market = new();
        /// <summary>Cards exiled from the market (dead monsters, RBG/Cataclysm exiles) — kept for conservation.</summary>
        public List<CardInstance> MarketExile = new();
        public CardInstance Boss;
        public DeterministicRng Rng;

        public int TurnPlayerIndex;
        public Phase Phase = Phase.Untap;
        public int Round = 1;

        public GameStack Stack = new();
        public ContinuousEffects Continuous = new();

        public bool GameOver;
        public int WinnerIndex = -1;

        // Monotonic counters.
        public long TimestampCounter;
        public int NextInstanceId = 1;
        public int NextStackItemId = 1;
        public int NextDecisionId = 1;

        public PlayerState TurnPlayer => Players[TurnPlayerIndex];

        public long NextTimestamp() => ++TimestampCounter;

        /// <summary>Find a card instance anywhere in the game (players' zones, market, stack, boss).</summary>
        public CardInstance FindCard(int instanceId)
        {
            foreach (var p in Players)
            {
                foreach (var c in p.Deck) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Hand) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Discard) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Exile) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.PlayedThisTurn) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Relics) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Equipment) if (c != null && c.InstanceId == instanceId) return c;
            }
            for (int t = 0; t < Market.Tiers; t++)
            {
                foreach (var c in Market.Piles[t]) if (c.InstanceId == instanceId) return c;
                foreach (var c in Market.Rows[t]) if (c != null && c.InstanceId == instanceId) return c;
            }
            foreach (var item in Stack.Items)
                if (item.SpellCard != null && item.SpellCard.InstanceId == instanceId) return item.SpellCard;
            foreach (var c in MarketExile)
                if (c.InstanceId == instanceId) return c;
            if (Boss != null && Boss.InstanceId == instanceId) return Boss;
            return null;
        }

        /// <summary>FNV-1a hash of the rules-relevant state, for replay/determinism verification.</summary>
        public ulong ComputeHash()
        {
            ulong h = 14695981039346656037UL;

            void Mix(long v)
            {
                unchecked
                {
                    for (int i = 0; i < 8; i++)
                    {
                        h ^= (byte)(v >> (i * 8));
                        h *= 1099511628211UL;
                    }
                }
            }

            void MixStr(string s)
            {
                if (s == null) { Mix(-1); return; }
                foreach (char c in s) Mix(c);
            }

            void MixCard(CardInstance c)
            {
                if (c == null) { Mix(-2); return; }
                Mix(c.InstanceId);
                MixStr(c.DefId);
                Mix((int)c.Zone);
                Mix(c.Tapped ? 1 : 0);
                Mix(c.MarkedDamage);
            }

            Mix(TurnPlayerIndex);
            Mix((int)Phase);
            Mix(Round);
            Mix((long)Rng.State);
            Mix(GameOver ? 1 : 0);
            Mix(WinnerIndex);

            foreach (var p in Players)
            {
                Mix(p.Level); Mix(p.Xp); Mix(p.Position); Mix(p.Ap); Mix(p.DamagePool);
                Mix(p.LastInnCheckpoint);
                foreach (int inn in p.ClaimedInns) Mix(inn);
                foreach (var c in p.Deck) MixCard(c);
                foreach (var c in p.Hand) MixCard(c);
                foreach (var c in p.Discard) MixCard(c);
                foreach (var c in p.Exile) MixCard(c);
                foreach (var c in p.PlayedThisTurn) MixCard(c);
                foreach (var c in p.Relics) MixCard(c);
                foreach (var c in p.Equipment) MixCard(c);
            }

            for (int t = 0; t < Market.Tiers; t++)
            {
                foreach (var c in Market.Piles[t]) MixCard(c);
                foreach (var c in Market.Rows[t]) MixCard(c);
            }

            foreach (var c in MarketExile) MixCard(c);
            MixCard(Boss);
            foreach (var item in Stack.Items)
            {
                Mix(item.Id);
                Mix((int)item.Kind);
                Mix(item.ControllerIndex);
                MixCard(item.SpellCard);
            }

            return h;
        }
    }
}
