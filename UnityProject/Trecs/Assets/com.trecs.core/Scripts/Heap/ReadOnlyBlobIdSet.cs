using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view over the set of <see cref="BlobId"/>s currently kept alive by
    /// one or more outstanding handles. Passed to <see cref="IBlobStore.CleanCache"/>
    /// and <see cref="IBlobStore.SumInMemoryInactiveTotals"/> so implementations can
    /// distinguish active (pinned) blobs from inactive (evictable) ones without
    /// allocating a separate <c>IterableHashSet&lt;BlobId&gt;</c>.
    /// <para>
    /// Backed directly by <see cref="BlobCache"/>'s ref-count dictionary, whose keyset
    /// <i>is</i> the active set — a blob is active iff at least one handle pins it,
    /// which is exactly when the dictionary contains it. Lifetime is bound to the
    /// owning <see cref="BlobCache"/>: the view is valid only for the duration of the
    /// call it was passed into; do not retain it past the callee's return.
    /// </para>
    /// </summary>
    public readonly struct ReadOnlyBlobIdSet
    {
        readonly IterableDictionary<BlobId, int> _refCounts;

        internal ReadOnlyBlobIdSet(IterableDictionary<BlobId, int> refCounts)
        {
            TrecsDebugAssert.IsNotNull(refCounts);
            _refCounts = refCounts;
        }

        /// <summary>Number of distinct active <see cref="BlobId"/>s.</summary>
        public int Count
        {
            get { return _refCounts.Count; }
        }

        public bool IsEmpty
        {
            get { return _refCounts.Count == 0; }
        }

        /// <summary>O(1) membership test.</summary>
        public bool Contains(BlobId id)
        {
            return _refCounts.ContainsKey(id);
        }

        /// <summary>Enumerate the active <see cref="BlobId"/>s.</summary>
        public IterableDictionaryKeyEnumerator<BlobId> GetEnumerator()
        {
            return _refCounts.Keys.GetEnumerator();
        }
    }
}
