using System;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// Deterministic random number generator using xoshiro128** algorithm.
    /// Reference: https://prng.di.unimi.it/ (public domain / CC0)
    /// Seeded via SplitMix64 to expand a single ulong seed into 128-bit state.
    /// </summary>
    public class Rng
    {
        uint _s0,
            _s1,
            _s2,
            _s3;

        public Rng(ulong? seed = null)
        {
            // Use SplitMix64 to expand the seed into 128 bits of state
            ulong sm = seed ?? (ulong)Environment.TickCount;
            ulong t0 = SplitMix64(ref sm);
            ulong t1 = SplitMix64(ref sm);
            _s0 = (uint)t0;
            _s1 = (uint)(t0 >> 32);
            _s2 = (uint)t1;
            _s3 = (uint)(t1 >> 32);
            // Ensure state is not all-zero
            if ((_s0 | _s1 | _s2 | _s3) == 0)
            {
                _s1 = 1;
            }
        }

        Rng(uint s0, uint s1, uint s2, uint s3)
        {
            Assert.That((s0 | s1 | s2 | s3) != 0, "Rng state must not be all-zero");
            _s0 = s0;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
        }

        public static Rng FromState(uint s0, uint s1, uint s2, uint s3)
        {
            return new Rng(s0, s1, s2, s3);
        }

        public (uint s0, uint s1, uint s2, uint s3) GetState()
        {
            return (_s0, _s1, _s2, _s3);
        }

        public void SetState(uint s0, uint s1, uint s2, uint s3)
        {
            Assert.That((s0 | s1 | s2 | s3) != 0, "Rng state must not be all-zero");
            _s0 = s0;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
        }

        public Rng Fork()
        {
            return new Rng(NextUlong());
        }

        static ulong SplitMix64(ref ulong state)
        {
            ulong z = state += 0x9e3779b97f4a7c15ul;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ul;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebul;
            return z ^ (z >> 31);
        }

        static uint RotateLeft(uint x, int k)
        {
            return (x << k) | (x >> (32 - k));
        }

        uint NextState()
        {
            uint result = RotateLeft(_s1 * 5, 7) * 9;
            uint t = _s1 << 9;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;

            _s2 ^= t;
            _s3 = RotateLeft(_s3, 11);

            return result;
        }

        /// <summary>
        /// Random number between 0 and 1
        /// </summary>
        public float Next()
        {
            return (NextUint() >> 8) * (1.0f / 16777216.0f);
        }

        public float NextFloat(float minValue, float maxValue)
        {
            Assert.That(maxValue >= minValue);
            return minValue + (maxValue - minValue) * Next();
        }

        public long NextLong()
        {
            uint high = NextUint();
            uint low = NextUint();
            return ((long)high << 32) | low;
        }

        public ulong NextUlong()
        {
            uint high = NextUint();
            uint low = NextUint();
            return ((ulong)high << 32) | low;
        }

        public uint NextUint()
        {
            return NextState();
        }

        public uint NextUint(uint exclusiveMax)
        {
            Assert.That(exclusiveMax > 0);

            // Debiased modulo reduction
            uint threshold = ((uint)-exclusiveMax) % exclusiveMax;

            while (true)
            {
                uint r = NextUint();

                if (r >= threshold)
                {
                    return r % exclusiveMax;
                }
            }
        }

        public bool NextBool()
        {
            return (NextUint() & 1) == 0;
        }

        public int NextInt(int minValue, int maxValueExclusive)
        {
            Assert.That(maxValueExclusive > minValue);

            long range = (long)maxValueExclusive - (long)minValue;
            uint boundedRange = range <= uint.MaxValue ? (uint)range : uint.MaxValue - 1;

            return (int)(minValue + NextUint(boundedRange));
        }

        public int NextInt()
        {
            return NextInt(int.MinValue + 1, int.MaxValue - 1);
        }
    }
}
