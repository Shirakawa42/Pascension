using System.Collections.Generic;

namespace Pascension.Engine.Core
{
    /// <summary>
    /// PCG32 random generator. The ONLY source of randomness allowed in the rules engine;
    /// state is plain data so replays and host/client hashes stay reproducible.
    /// </summary>
    public sealed class DeterministicRng
    {
        private const ulong Multiplier = 6364136223846793005UL;

        public ulong State;
        public ulong Inc;

        public DeterministicRng(ulong seed, ulong sequence = 54UL)
        {
            State = 0;
            Inc = (sequence << 1) | 1UL;
            NextUInt();
            State += seed;
            NextUInt();
        }

        public uint NextUInt()
        {
            ulong old = State;
            State = old * Multiplier + Inc;
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
        }

        /// <summary>Uniform value in [0, maxExclusive). maxExclusive must be &gt; 0.</summary>
        public int Next(int maxExclusive)
        {
            // Rejection sampling to avoid modulo bias.
            uint bound = (uint)maxExclusive;
            uint threshold = (uint)(-bound) % bound;
            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold) return (int)(r % bound);
            }
        }

        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
