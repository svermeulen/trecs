using System;
using System.Runtime.CompilerServices;

namespace Trecs.Collections
{
    /// <summary>
    /// Wraps a reference type (class/interface/string) so it can be used as a key
    /// in <see cref="IterableDictionary{TKey,TValue}"/> (which requires struct keys).
    /// Implicit conversions both ways make usage transparent at call sites.
    /// </summary>
    public readonly struct RefKey<T> : IEquatable<RefKey<T>>
        where T : class
    {
        public readonly T Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RefKey(T value)
        {
            Value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(RefKey<T> other)
        {
            if (ReferenceEquals(Value, other.Value))
                return true;
            if (Value == null || other.Value == null)
                return false;
            return Value.Equals(other.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            return Value?.GetHashCode() ?? 0;
        }

        public override bool Equals(object obj)
        {
            return obj is RefKey<T> other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator RefKey<T>(T value) => new(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator T(RefKey<T> key) => key.Value;

        public override string ToString() => Value?.ToString() ?? "null";
    }
}
