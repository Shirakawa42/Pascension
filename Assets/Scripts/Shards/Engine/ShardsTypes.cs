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
        Homodeus,   // champions-in-play synergy (Inspire, from RotF)
        Undergrowth,// healing / ally chains (Unify)
        Order,      // mastery, shields, draw (Dominion)
        Wraethe,    // discard-pile synergy, banish (Echo, from RotF)
        Aion,       // 5th faction (Shadow of Salvation / Into the Horizon)
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
        SetAside,     // relics waiting to be earned (RotF)
        DestinyRow,   // shared face-up destiny row (ItH)
        MonsterSpace, // revealed Ingeminex beside the row (ItH)
        Banished      // shared removed-from-game pile
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
        /// <summary>Fast-played (or warped) — returns to the bottom of the CENTER deck at cleanup.</summary>
        public bool FastPlayed;
        /// <summary>Champion/Ingeminex damage marked THIS turn — clears every end phase.</summary>
        public int DamageThisTurn;

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
        /// <summary>Shield value — revealed FROM HAND to prevent damage (champions'
        /// printed shields are inert while in play, base game).</summary>
        public int Shield;
        /// <summary>Praetorian-02 exception: shield works while IN PLAY instead of from hand.</summary>
        public bool ShieldInPlay;
        /// <summary>Owning character id for relics/destinies.</summary>
        public string Character;

        /// <summary>Played (allies/champions/mercenaries) or recruited (relics) effect.</summary>
        public IShardsEffect PlayEffect;
        /// <summary>Champion/character once-per-turn exhaust ability.</summary>
        public IShardsEffect ExhaustEffect;
        /// <summary>Gems paid as part of the exhaust COST ("Pay N gems, Exhaust:") —
        /// the activation is illegal while unaffordable (Shard Defiant, Whatever it
        /// Takes). Effects never check gems themselves.</summary>
        public int ExhaustGemCost;
        /// <summary>Monster kill reward (Into the Horizon).</summary>
        public IShardsEffect RewardEffect;
        /// <summary>Ingeminex: fires once, end of the reveal turn, against ALL players.</summary>
        public IShardsEffect MonsterAttackEffect;

        // ---- static hooks (defs are code-built, never serialized) ----

        /// <summary>Champion targeting veto: (state, attacker, owner, champion) — null = always attackable.
        /// (Li Hin: never with power; Raidian: attacker needs mastery ≥ owner's; Drakonarius:
        /// not while owner controls General Decurion.)</summary>
        public System.Func<ShardsState, ShardsPlayer, ShardsPlayer, ShardsCard, bool> CanBeAttacked;
        /// <summary>Zetta: while in play, the owner and their OTHER champions can't be attacked
        /// (end-turn damage can't be assigned to the owner either).</summary>
        public bool Taunt;
        /// <summary>Defense bonus this card (while in owner's play/destiny zone) grants a
        /// champion: (owner, sourceCard, champion) → bonus. Ferrata Guard, One Mind One Army.</summary>
        public System.Func<ShardsPlayer, ShardsCard, ShardsCard, int> DefenseAura;
        /// <summary>Buy-cost adjustment while in the row: (buyer) → delta. Axia.</summary>
        public System.Func<ShardsPlayer, int> CostModifier;
        /// <summary>Praetorian-01: returns from the owner's discard to hand when they play a champion.</summary>
        public bool ReturnsFromDiscardOnChampionPlay;
        /// <summary>Owned-destiny trigger on unprevented player damage: (dealt) → effect or null.
        /// Blood for Blood.</summary>
        public System.Func<int, IShardsEffect> OnDamageDealt;
        /// <summary>Swyft: while in play and the owner's character matches this id, the owner
        /// may keep fast-played cards (→ discard) instead of returning them at cleanup.</summary>
        public string KeepFastPlaysCharacter;
        /// <summary>Maglev Tunnels: owned destiny lets Homodeus champion recruits go on top
        /// of the deck instead of discard (owner's choice).</summary>
        public bool RedirectChampionRecruitsToDeckTop;
        /// <summary>Breaker: recruits go to the buyer's HAND instead of discard.</summary>
        public bool RecruitsToHand;
        /// <summary>Datic Robes (shield = your mastery), Praetorian-02 M20 — overrides the
        /// printed Shield value: (owner) → value.</summary>
        public System.Func<ShardsPlayer, int> DynamicShield;
        /// <summary>The Dispossessed: while in the discard pile, playing a card of this
        /// faction lets the owner return it to hand (optional).</summary>
        public ShardsFaction ReturnFromDiscardOnFactionPlay = ShardsFaction.None;

        public string RulesText = "";
        public string ArtPrompt = "";

        /// <summary>Champions plus relic champions (Praetorian-02): they deploy to the
        /// champion zone when played and persist across turns.</summary>
        public bool IsChampion => Type == ShardsCardType.Champion ||
                                  (Type == ShardsCardType.Relic && Defense > 0);
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
