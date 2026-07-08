namespace Pascension.Engine.Targeting
{
    public enum TargetSpecKind
    {
        /// <summary>A monster occupying a market row slot (the boss is NOT included).</summary>
        Monster,
        /// <summary>A spell (not ability) on the stack. Excludes the targeting spell itself.</summary>
        SpellOnStack,
        /// <summary>Another player (not the controller).</summary>
        Opponent,
        /// <summary>A relic or equipped equipment belonging to any player.</summary>
        RelicOrEquipment
    }

    /// <summary>
    /// Declares what a spell/ability targets. Options are enumerated when the card is
    /// played (the engine raises a ChooseTargets decision) and re-validated on resolution;
    /// if every target is gone the item fizzles.
    /// </summary>
    public sealed class TargetSpec
    {
        public TargetSpecKind Kind;
        public string Description;

        public TargetSpec(TargetSpecKind kind, string description)
        {
            Kind = kind;
            Description = description;
        }

        public static TargetSpec Monster(string desc = "Target monster") => new(TargetSpecKind.Monster, desc);
        public static TargetSpec Spell(string desc = "Target spell") => new(TargetSpecKind.SpellOnStack, desc);
        public static TargetSpec Opponent(string desc = "Target player") => new(TargetSpecKind.Opponent, desc);
        public static TargetSpec RelicOrEquipment(string desc = "Target relic or equipment") => new(TargetSpecKind.RelicOrEquipment, desc);
    }
}
