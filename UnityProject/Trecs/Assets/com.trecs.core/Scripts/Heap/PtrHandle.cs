using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Opaque handle to a heap-allocated pointer slot. Used as the component-stored reference
    /// into <see cref="UniquePtr{T}"/>, <see cref="SharedPtr{T}"/>, <see cref="NativeUniquePtr{T}"/>,
    /// and <see cref="NativeSharedPtr{T}"/>. A zero value represents a null handle.
    /// </summary>
    [TypeId(604918273)]
    public struct PtrHandle : IEquatable<PtrHandle>, IStableHashProvider
    {
        public uint Value;

        public PtrHandle(uint value)
        {
            Value = value;
        }

        public readonly bool IsNull
        {
            get { return Value == 0; }
        }

        public readonly bool Equals(PtrHandle other)
        {
            return Value == other.Value;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is PtrHandle other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return GetStableHashCode();
        }

        public readonly int GetStableHashCode()
        {
            return unchecked((int)Value);
        }

        public static bool operator ==(PtrHandle left, PtrHandle right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PtrHandle left, PtrHandle right)
        {
            return !left.Equals(right);
        }

        public override readonly string ToString()
        {
            return Value.ToString();
        }
    }
}
