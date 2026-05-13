using System;

namespace Trecs
{
    /// <summary>
    /// Bundles the (pointer, allocation size, alignment) triple required by the
    /// taking-ownership blob APIs. Use the constructor to build one from the
    /// raw values you got back from a native allocator, or build one alongside
    /// any helper that already exposes those three fields.
    /// </summary>
    public readonly struct NativeBlobAllocation : IEquatable<NativeBlobAllocation>
    {
        public readonly IntPtr Ptr;
        public readonly int AllocSize;
        public readonly int Alignment;

        public NativeBlobAllocation(IntPtr ptr, int allocSize, int alignment)
        {
            Ptr = ptr;
            AllocSize = allocSize;
            Alignment = alignment;
        }

        public static readonly NativeBlobAllocation Null = default;

        public bool IsNull
        {
            get { return Ptr == IntPtr.Zero; }
        }

        public bool Equals(NativeBlobAllocation other)
        {
            return Ptr == other.Ptr && AllocSize == other.AllocSize && Alignment == other.Alignment;
        }

        public override bool Equals(object obj)
        {
            return obj is NativeBlobAllocation other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Ptr, AllocSize, Alignment);
        }

        public static bool operator ==(NativeBlobAllocation a, NativeBlobAllocation b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(NativeBlobAllocation a, NativeBlobAllocation b)
        {
            return !a.Equals(b);
        }
    }
}
