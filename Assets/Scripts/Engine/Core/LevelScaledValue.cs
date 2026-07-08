namespace Pascension.Engine.Core
{
    /// <summary>
    /// An amount that scales with hero level in the GDD's three brackets:
    /// levels 1-4, levels 5-9, and level 10 (e.g. Redbull: +2 / +3 / +5 AP).
    /// </summary>
    public readonly struct LevelScaledValue
    {
        public readonly int Low;
        public readonly int Mid;
        public readonly int High;

        public LevelScaledValue(int low, int mid, int high)
        {
            Low = low;
            Mid = mid;
            High = high;
        }

        public static LevelScaledValue Flat(int value) => new(value, value, value);

        public int For(int level) => level >= 10 ? High : level >= 5 ? Mid : Low;

        public bool IsFlat => Low == Mid && Mid == High;

        public override string ToString() => IsFlat ? Low.ToString() : $"{Low}/{Mid}/{High}";
    }
}
