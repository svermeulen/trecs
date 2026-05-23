using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Value-equality wrapper around an array, for use in Roslyn incremental-generator
    /// pipeline models. <see cref="ImmutableArray{T}"/> compares by reference identity of
    /// the underlying array; that breaks pipeline caching because two transforms producing
    /// the same logical sequence return different array instances. Wrapping the data in
    /// <see cref="EquatableArray{T}"/> gives the engine structural equality, which lets
    /// downstream nodes skip re-running when nothing observable changed.
    ///
    /// <para>Element type must itself implement <see cref="IEquatable{T}"/> — otherwise
    /// the array is only as good as element identity, and the equality check silently
    /// degenerates back to the original problem.</para>
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        public static readonly EquatableArray<T> Empty = new(Array.Empty<T>());

        private readonly T[]? _items;

        public EquatableArray(T[] items)
        {
            _items = items;
        }

        public int Length => _items?.Length ?? 0;
        public bool IsEmpty => Length == 0;

        public T this[int index] => _items![index];

        public bool Equals(EquatableArray<T> other)
        {
            var a = _items;
            var b = other._items;
            if (ReferenceEquals(a, b))
                return true;
            if (a is null)
                return b!.Length == 0;
            if (b is null)
                return a.Length == 0;
            if (a.Length != b.Length)
                return false;
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a.Length; i++)
            {
                if (!comparer.Equals(a[i], b[i]))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            if (_items is null)
                return 0;
            unchecked
            {
                int hash = 17;
                foreach (var item in _items)
                    hash = hash * 31 + (item is null ? 0 : item.GetHashCode());
                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public T[] ToArray() => _items is null ? Array.Empty<T>() : (T[])_items.Clone();

        public ImmutableArray<T> ToImmutableArray() =>
            _items is null ? ImmutableArray<T>.Empty : ImmutableArray.Create(_items);

        public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) =>
            left.Equals(right);

        public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) =>
            !left.Equals(right);
    }

    internal static class EquatableArrayExtensions
    {
        public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source)
            where T : IEquatable<T> => new(source.ToArray());

        public static EquatableArray<T> ToEquatableArray<T>(this T[] source)
            where T : IEquatable<T> => new(source);
    }
}
