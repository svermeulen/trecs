using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

// Cannot be Trecs.Internal since it is used in jobs

namespace Trecs
{
    /// <summary>
    /// Read-only view over the entity indices belonging to one group within a set.
    /// Used internally by query iterators during iteration. Because this is a
    /// <c>ref struct</c>, it cannot be stored as a job field — use
    /// <see cref="NativeEntitySetIndices{TSet}"/> for job-compatible access.
    /// </summary>
    public ref struct EntitySetIndices
    {
        readonly NativeBuffer<int> _indices;
        readonly int _count;

        // Captured reference to the source dict's live count, used in DEBUG to
        // detect AddImmediate / RemoveImmediate / Clear of the same group while
        // iterating. The dict struct shares native memory with the source, so
        // reading Count off this copy reflects mutations on the original.
        [NativeDisableContainerSafetyRestriction]
        readonly NativeDenseDictionary<int, int> _sourceDict;

        public EntitySetIndices(
            NativeBuffer<int> indices,
            int count,
            in NativeDenseDictionary<int, int> sourceDict
        )
        {
            _indices = indices;
            _count = count;
            _sourceDict = sourceDict;
        }

        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        internal readonly NativeBuffer<int> Buffer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _indices;
        }

        public readonly int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                AssertNotMutatedDuringIteration();
                return _indices.IndexAsReadOnly(index);
            }
        }

        [Conditional("DEBUG")]
        readonly void AssertNotMutatedDuringIteration()
        {
            // _sourceDict is default for callers that haven't been migrated yet
            // (e.g. user code constructing EntitySetIndices directly). Skip the
            // check rather than firing a misleading assert in those cases.
            if (!_sourceDict.IsCreated)
                return;

            Assert.That(
                _sourceDict.Count == _count,
                "Set entry mutated during iteration. AddImmediate / RemoveImmediate / "
                    + "Clear on the same set + same group is not allowed while iterating that "
                    + "group. Use deferred SetAdd / SetRemove instead, or stage the mutations "
                    + "in a separate buffer and apply them after the iteration completes."
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Enumerator GetEnumerator() => new Enumerator(_indices, _count, _sourceDict);

        public ref struct Enumerator
        {
            readonly NativeBuffer<int> _indices;
            readonly int _count;

            [NativeDisableContainerSafetyRestriction]
            readonly NativeDenseDictionary<int, int> _sourceDict;

            int _position;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(
                NativeBuffer<int> indices,
                int count,
                in NativeDenseDictionary<int, int> sourceDict
            )
            {
                _indices = indices;
                _count = count;
                _sourceDict = sourceDict;
                _position = -1;
            }

            public int Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _indices.IndexAsReadOnly(_position);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                AssertNotMutatedDuringIteration();
                return ++_position < _count;
            }

            [Conditional("DEBUG")]
            readonly void AssertNotMutatedDuringIteration()
            {
                if (!_sourceDict.IsCreated)
                    return;

                Assert.That(
                    _sourceDict.Count == _count,
                    "Set entry mutated during iteration. AddImmediate / RemoveImmediate / "
                        + "Clear on the same set + same group is not allowed while iterating "
                        + "that group. Use deferred SetAdd / SetRemove instead, or stage the "
                        + "mutations in a separate buffer and apply them after the iteration "
                        + "completes."
                );
            }
        }
    }
}
