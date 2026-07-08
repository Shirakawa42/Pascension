using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Heroes;

namespace Pascension.Engine.Core
{
    public sealed class PlayerState
    {
        public int Index;
        public string Name = "";
        public string HeroId;

        public int Level = 1;
        public int Xp;
        /// <summary>Board position, 0 (start) .. 50 (boss step).</summary>
        public int Position;
        /// <summary>Furthest inn step reached; forced move-back cannot go below this.</summary>
        public int LastInnCheckpoint;
        /// <summary>Inn steps whose reward was already claimed. Sorted list for determinism.</summary>
        public List<int> ClaimedInns = new();

        public int Ap;
        public int DamagePool;

        // Zones. Deck index 0 = top. Equipment slot order: Weapon, Armor, Trinket.
        public List<CardInstance> Deck = new();
        public List<CardInstance> Hand = new();
        public List<CardInstance> Discard = new();
        public List<CardInstance> Exile = new();
        public List<CardInstance> PlayedThisTurn = new();
        public List<CardInstance> Relics = new();
        public CardInstance[] Equipment = new CardInstance[3];

        // Per-turn bookkeeping (reset in TurnController.StartTurn). Consulted by static abilities.
        public int BuysThisTurn;
        public int MovesThisTurn;
        public int DamageCardsThisTurn;
        public bool HeroActiveUsedThisTurn;
        public bool HeroUltimateUsedThisTurn;
        public bool NextBuyToHand;
        public int PendingExtraTurns;

        // Session preferences / status.
        public bool FullControl;
        public bool Conceded;

        public HeroDefinition Hero => HeroDatabase.Get(HeroId);

        public CardInstance EquipmentIn(EquipSlot slot) => slot switch
        {
            EquipSlot.Weapon => Equipment[0],
            EquipSlot.Armor => Equipment[1],
            EquipSlot.Trinket => Equipment[2],
            _ => null
        };

        public void SetEquipment(EquipSlot slot, CardInstance card)
        {
            int i = slot switch { EquipSlot.Weapon => 0, EquipSlot.Armor => 1, _ => 2 };
            Equipment[i] = card;
        }

        /// <summary>All permanents in play for this player (equipment then relics), in stable order.</summary>
        public IEnumerable<CardInstance> Permanents()
        {
            foreach (var e in Equipment)
                if (e != null)
                    yield return e;
            foreach (var r in Relics)
                yield return r;
        }
    }
}
