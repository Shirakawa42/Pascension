using Pascension.Engine.Core;

namespace Pascension.Engine.Cards
{
    /// <summary>
    /// A physical copy of a card in the game. Pure data; the definition is looked up
    /// through <see cref="CardDatabase"/> by <see cref="DefId"/>.
    /// </summary>
    public sealed class CardInstance
    {
        public int InstanceId;
        public string DefId;
        /// <summary>Owning player index, or -1 for market/boss cards.</summary>
        public int Owner = -1;
        public ZoneType Zone;
        /// <summary>Occupied equipment slot while Zone == Equipment.</summary>
        public EquipSlot Slot = EquipSlot.None;
        public bool Tapped;
        /// <summary>Damage marked on this monster/boss this turn (clears at end of turn).</summary>
        public int MarkedDamage;
        /// <summary>Player index that most recently assigned damage (kill attribution).</summary>
        public int LastDamagedBy = -1;
        /// <summary>Monotonic ordering stamp (equipment replacement, modifier ordering).</summary>
        public long Timestamp;

        public CardDefinition Def => CardDatabase.Get(DefId);

        public override string ToString() => $"{DefId}#{InstanceId}({Zone})";
    }
}
