using System;
using System.Collections.Generic;
using Pascension.Core;

namespace Shards.Engine
{
    [Flags]
    public enum ShardsDlc
    {
        None = 0,
        RelicsOfTheFuture = 1,
        ShadowOfSalvation = 2,
        IntoTheHorizon = 4
    }

    public enum ShardsFaction
    {
        None,       // starters, some neutral cards
        Homodeus,
        Undergrowth,
        Order,
        Wraith,     // TODO-VERIFY exact 4th base faction name from rules-notes (M4)
        Monster     // Into the Horizon Ingeminex
    }

    public enum ShardsCardType
    {
        Ally,
        Champion,
        Mercenary,
        Monster,
        Relic,
        Destiny,
        Starter
    }

    public enum ShardsZone
    {
        CenterDeck,
        CenterRow,
        Deck,
        Hand,
        Discard,
        PlayZone,
        Champions,
        SetAside,   // relics/destinies waiting to be earned
        Removed
    }

    /// <summary>A physical card in the game (SoI has no tapping of hand cards — only
    /// champions and the character card exhaust).</summary>
    public sealed class ShardsCard
    {
        public int InstanceId;
        public string DefId;
        /// <summary>-1 for center-owned cards.</summary>
        public int Owner = -1;
        public ShardsZone Zone;
        /// <summary>Champions/character: used an exhaust ability this turn.</summary>
        public bool Exhausted;
        /// <summary>Fast-played mercenary — returns to the bottom of the CENTER deck at cleanup.</summary>
        public bool FastPlayed;

        public ShardsCardDef Def => ShardsCardDatabase.Get(DefId);
    }

    /// <summary>Immutable card definition. Effects are data-composed (mastery thresholds,
    /// faction triggers) — rules text is a display string only, never parsed.</summary>
    public sealed class ShardsCardDef
    {
        public string Id;
        public string Name;
        public string Set = "base";
        public ShardsFaction Faction;
        public ShardsCardType Type;
        public int Cost;
        /// <summary>Copies in the center deck (or starter deck).</summary>
        public int Quantity = 1;
        /// <summary>Champions/monsters: power required to destroy it.</summary>
        public int Defense;
        /// <summary>Shield value (hand-reveal for allies marked as shields; passive for champions).</summary>
        public int Shield;
        /// <summary>Owning character id for relics/destinies.</summary>
        public string Character;

        /// <summary>Played (allies/champions/mercenaries) or recruited (relics) effect.</summary>
        public IShardsEffect PlayEffect;
        /// <summary>Champion/character once-per-turn exhaust ability.</summary>
        public IShardsEffect ExhaustEffect;
        /// <summary>Monster kill reward (Into the Horizon).</summary>
        public IShardsEffect RewardEffect;

        public string RulesText = "";
        public string ArtPrompt = "";

        public bool IsChampion => Type == ShardsCardType.Champion;
        public bool IsMonster => Type == ShardsCardType.Monster;
    }

    /// <summary>Static registry, separate from Pascension's card database (own id namespace).</summary>
    public static class ShardsCardDatabase
    {
        private static readonly Dictionary<string, ShardsCardDef> Cards = new();

        public static void Register(ShardsCardDef def) => Cards[def.Id] = def;
        public static ShardsCardDef Get(string id) => Cards[id];
        public static bool TryGet(string id, out ShardsCardDef def) => Cards.TryGetValue(id, out def);
        public static IEnumerable<ShardsCardDef> All => Cards.Values;
        public static int Count => Cards.Count;
        public static void Clear() => Cards.Clear();
    }

    public sealed class ShardsConfig
    {
        public ulong Seed;
        public ShardsDlc Dlc;
        public List<PlayerSpec> Players = new();
        public ShardsRules Rules = new();
    }

    /// <summary>Fixed rule constants (kept as data so the client rules handoff works).</summary>
    public sealed class ShardsRules
    {
        public int StartingHealth = 50;
        public int MaxHealth = 50;
        public int HandSize = 5;
        public int MasteryCap = 30;
        public int CenterRowSize = 6;
        public float ResponseTimerSeconds = 0f;
    }
}
