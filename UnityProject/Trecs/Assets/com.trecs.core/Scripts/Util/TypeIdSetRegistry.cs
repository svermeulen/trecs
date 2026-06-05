using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Trecs.Internal
{
    /// <summary>
    /// Shared intern table for <see cref="TypeIdSet"/>. Members are stored sorted by
    /// <see cref="TypeId.Value"/> so XOR-hash collisions can be detected on cache hit.
    /// Backs both <see cref="ComponentTypeIdSet"/> and <see cref="TagSet"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class TypeIdSetRegistry
    {
        struct SetEntry
        {
            // Always populated when the entry is in the dict (set on insertion).
            public TypeId[] Members;

            // Lazily filled by SetToString on first call; null until then.
            public string DebugString;
        }

        static readonly Dictionary<int, SetEntry> _entries;

        // Main-thread only — every public entry asserts UnityThreadHelper.IsMainThread.
        static int[] _sortBuffer = new int[16];

        static TypeIdSetRegistry()
        {
            _entries = new Dictionary<int, SetEntry>(
                new Dictionary<int, SetEntry>
                {
                    {
                        TypeIdSet.Null.Id,
                        new SetEntry { Members = Array.Empty<TypeId>(), DebugString = "Null" }
                    },
                }
            );
        }

        public static TypeIdSet FromSingle(TypeId member)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            int id = member.Value;
            if (id == 0)
            {
                throw EmptyCollisionException();
            }

            if (_entries.TryGetValue(id, out var existing))
            {
                AssertMembersMatch(id, existing.Members, stackalloc int[] { member.Value });
                return new TypeIdSet(id);
            }

            _entries.Add(id, new SetEntry { Members = new[] { member } });
            return new TypeIdSet(id);
        }

        public static TypeIdSet AddMember(TypeIdSet existing, TypeId member)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            TrecsDebugAssert.That(!existing.IsNull, "Use FromSingle for null base set");

            var existingMembers = _entries[existing.Id].Members;

            for (int i = 0; i < existingMembers.Length; i++)
            {
                if (existingMembers[i].Equals(member))
                {
                    return existing;
                }
            }

            // No id is ever remapped, so a stored id always equals the exact XOR of its
            // members; existing.Id ^ member.Value is therefore the true new-set id. A 0
            // result would be a non-empty set colliding with the empty/null set (id 0) —
            // an astronomically rare XOR coincidence, surfaced as a loud throw rather than
            // silently remapped (which used to break every XOR-against-id consumer).
            int newId = existing.Id ^ member.Value;
            if (newId == 0)
            {
                throw EmptyCollisionException();
            }

            if (_entries.TryGetValue(newId, out var existingNewEntry))
            {
                AssertCombinedMembersMatch(
                    newId,
                    existingNewEntry.Members,
                    existingMembers,
                    member
                );
                return new TypeIdSet(newId);
            }

            var combined = new TypeId[existingMembers.Length + 1];
            for (int i = 0; i < existingMembers.Length; i++)
            {
                combined[i] = existingMembers[i];
            }
            combined[existingMembers.Length] = member;
            SortInPlace(combined);
            _entries.Add(newId, new SetEntry { Members = combined });
            return new TypeIdSet(newId);
        }

        public static TypeIdSet Combine(TypeIdSet a, TypeIdSet b)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            var membersA = _entries[a.Id].Members;
            var membersB = _entries[b.Id].Members;

            int ia = 0,
                ib = 0,
                hash = 0;
            while (ia < membersA.Length && ib < membersB.Length)
            {
                int va = membersA[ia].Value;
                int vb = membersB[ib].Value;
                if (va < vb)
                {
                    hash ^= va;
                    ia++;
                }
                else if (va > vb)
                {
                    hash ^= vb;
                    ib++;
                }
                else
                {
                    hash ^= va;
                    ia++;
                    ib++;
                }
            }
            while (ia < membersA.Length)
                hash ^= membersA[ia++].Value;
            while (ib < membersB.Length)
                hash ^= membersB[ib++].Value;

            if (hash == 0)
            {
                throw EmptyCollisionException();
            }

            if (_entries.TryGetValue(hash, out var existing))
            {
                // Allocation-free collision check: merge into the shared buffer (no array
                // alloc, unlike MergeSorted) and compare against the interned members.
                int count = MergeIntoSortBuffer(membersA, membersB);
                AssertMembersMatch(
                    hash,
                    existing.Members,
                    new ReadOnlySpan<int>(_sortBuffer, 0, count)
                );
                return new TypeIdSet(hash);
            }

            var sorted = MergeSorted(membersA, membersB);
            _entries.Add(hash, new SetEntry { Members = sorted });
            return new TypeIdSet(hash);
        }

        public static IReadOnlyList<TypeId> GetMembers(TypeIdSet set)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            return _entries[set.Id].Members;
        }

        public static string SetToString(TypeIdSet set)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            var entry = _entries[set.Id];
            if (entry.DebugString != null)
            {
                return entry.DebugString;
            }

            entry.DebugString = string.Format(
                "{0} ({1})",
                entry.Members.Select(m => m.ToString()).OrderBy(s => s).Join("-"),
                set.Id
            );
            _entries[set.Id] = entry;
            return entry.DebugString;
        }

        static void SortInPlace(TypeId[] members)
        {
            if (_sortBuffer.Length < members.Length)
            {
                _sortBuffer = new int[members.Length];
            }
            for (int i = 0; i < members.Length; i++)
            {
                _sortBuffer[i] = members[i].Value;
            }
            Array.Sort(_sortBuffer, 0, members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                members[i] = new TypeId(_sortBuffer[i]);
            }
        }

        // Merges the two sorted, deduped member arrays into the shared _sortBuffer and
        // returns the merged length. Allocation-free; the caller must consume _sortBuffer
        // before any other registry call reuses it.
        static int MergeIntoSortBuffer(TypeId[] a, TypeId[] b)
        {
            int max = a.Length + b.Length;
            if (_sortBuffer.Length < max)
            {
                _sortBuffer = new int[max];
            }

            int ia = 0,
                ib = 0,
                count = 0;
            while (ia < a.Length && ib < b.Length)
            {
                int va = a[ia].Value;
                int vb = b[ib].Value;
                if (va < vb)
                {
                    _sortBuffer[count++] = va;
                    ia++;
                }
                else if (va > vb)
                {
                    _sortBuffer[count++] = vb;
                    ib++;
                }
                else
                {
                    _sortBuffer[count++] = va;
                    ia++;
                    ib++;
                }
            }
            while (ia < a.Length)
                _sortBuffer[count++] = a[ia++].Value;
            while (ib < b.Length)
                _sortBuffer[count++] = b[ib++].Value;

            return count;
        }

        static TypeId[] MergeSorted(TypeId[] a, TypeId[] b)
        {
            int count = MergeIntoSortBuffer(a, b);
            var result = new TypeId[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new TypeId(_sortBuffer[i]);
            }
            return result;
        }

        // A non-empty set whose members XOR to 0 collides with the empty/null set, which
        // owns id 0. We reserve id 0 for the empty set (so default(TypeIdSet) stays a safe
        // null sentinel and id == XOR(members) holds exactly for every other set), and
        // treat the collision as the same kind of (astronomically rare) XOR-hash collision
        // that AssertMembersMatch guards — a loud, always-on throw rather than a silent
        // remap. Keeping id == XOR(members) exact is what lets callers XOR against a stored
        // id directly (including in Burst, where the member list is unreachable).
        static TrecsException EmptyCollisionException() =>
            new TrecsException(
                "TypeIdSet XOR-hash collision: a non-empty set's members XOR to 0, which is "
                    + "reserved for the empty/null set (TypeIdSet.Null). This is an "
                    + "astronomically rare coincidence of member TypeId values. Resolve it by "
                    + "renaming one of the member types (in default mode a type's id is the "
                    + "hash of its name), or by changing a [TypeId] under "
                    + "TRECS_REQUIRE_EXPLICIT_TYPE_IDS."
            );

        // Always-on collision guard: the supplied sorted-int sequence must match the
        // already-interned set at this id. XOR-hash ids are non-injective, so two distinct
        // member sets can land on the same id; without this check the second one would
        // silently alias the first (wrong query/group results). The comparison is O(members)
        // and allocation-free, and the registry is a cold path (set construction / warmup,
        // not per-entity), so it runs in every build rather than DEBUG-only.
        static void AssertMembersMatch(int id, TypeId[] existing, ReadOnlySpan<int> sortedValues)
        {
            if (existing.Length == sortedValues.Length)
            {
                bool match = true;
                for (int i = 0; i < existing.Length; i++)
                {
                    if (existing[i].Value != sortedValues[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return;
                }
            }

            throw new TrecsException(
                $"TypeIdSet XOR-hash collision: id {id} is already interned for a different "
                    + "member set. This is an astronomically rare coincidence of member "
                    + "TypeId values. Resolve it by renaming one of the member types (in "
                    + "default mode a type's id is the hash of its name), or by changing a "
                    + "[TypeId] under TRECS_REQUIRE_EXPLICIT_TYPE_IDS."
            );
        }

        static void AssertCombinedMembersMatch(
            int id,
            TypeId[] existing,
            TypeId[] baseMembers,
            TypeId added
        )
        {
            int total = baseMembers.Length + 1;
            if (_sortBuffer.Length < total)
            {
                _sortBuffer = new int[total];
            }
            for (int i = 0; i < baseMembers.Length; i++)
            {
                _sortBuffer[i] = baseMembers[i].Value;
            }
            _sortBuffer[baseMembers.Length] = added.Value;
            Array.Sort(_sortBuffer, 0, total);
            AssertMembersMatch(id, existing, new ReadOnlySpan<int>(_sortBuffer, 0, total));
        }
    }
}
