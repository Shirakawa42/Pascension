namespace Pascension.Engine.Core
{
    public enum CardTier
    {
        Default = 0,
        Basic = 1,
        Advanced = 2,
        Elite = 3,
        Boss = 4
    }

    /// <summary>GDD card types. The GDD's "nothing" type is <see cref="Action"/> in code;
    /// its type line still displays "Nothing".</summary>
    public enum CardType
    {
        Action,
        Instant,
        Equipment,
        Relic,
        Monster
    }

    public enum EquipSlot
    {
        None,
        Weapon,
        Armor,
        Trinket
    }

    public enum Keyword
    {
        Ethereal
    }

    public enum Phase
    {
        Untap,
        Main,
        End
    }

    public enum ZoneType
    {
        Deck,
        Hand,
        Discard,
        Exile,
        PlayedThisTurn,
        Equipment,
        Relics,
        MarketRow,
        Pile,
        Stack,
        Boss
    }
}
