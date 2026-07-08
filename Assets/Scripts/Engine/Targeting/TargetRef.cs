using System;

namespace Pascension.Engine.Targeting
{
    public enum TargetKind
    {
        MonsterSlot,
        Boss,
        Player,
        StackItem,
        Card
    }

    /// <summary>
    /// A resolved reference to something targetable. Plain data so it serializes
    /// and can be re-validated at resolution time (targets can disappear).
    /// </summary>
    public readonly struct TargetRef : IEquatable<TargetRef>
    {
        public readonly TargetKind Kind;
        /// <summary>MonsterSlot: tier (1..3) · Player: player index · StackItem: stack item id · Card: instance id.</summary>
        public readonly int A;
        /// <summary>MonsterSlot: slot index (0..4). Unused otherwise.</summary>
        public readonly int B;

        private TargetRef(TargetKind kind, int a, int b = 0)
        {
            Kind = kind;
            A = a;
            B = b;
        }

        public static TargetRef Monster(int tier, int slot) => new(TargetKind.MonsterSlot, tier, slot);
        public static TargetRef TheBoss() => new(TargetKind.Boss, 0);
        public static TargetRef PlayerAt(int index) => new(TargetKind.Player, index);
        public static TargetRef Spell(int stackItemId) => new(TargetKind.StackItem, stackItemId);
        public static TargetRef CardById(int instanceId) => new(TargetKind.Card, instanceId);

        public bool Equals(TargetRef other) => Kind == other.Kind && A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is TargetRef other && Equals(other);
        public override int GetHashCode() => ((int)Kind * 397) ^ (A * 31) ^ B;
        public override string ToString() => $"{Kind}({A},{B})";
    }
}
