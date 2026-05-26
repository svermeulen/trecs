using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Shared list-management helpers for <see cref="WorldSnapshot"/>
    /// collections used by both <see cref="BundleRecorder"/> and the editor's
    /// <c>TrecsRewindBuffer</c>. Pulled out because both recorders maintain
    /// frame-sorted snapshot lists with replace-if-present-or-insert-sorted
    /// semantics and per-frame checksum dictionaries that need duplication-
    /// safe copies; keeping these in one place avoids the two recorders
    /// drifting in subtle ways.
    /// </summary>
    internal static class WorldSnapshotListUtil
    {
        /// <summary>
        /// Insert <paramref name="entry"/> into <paramref name="list"/>
        /// preserving frame-ascending order. If the list already contains an
        /// entry at the same <see cref="WorldSnapshot.FixedFrame"/>, that
        /// entry is replaced in place (callers are responsible for any
        /// cleanup of the displaced payload — see
        /// <see cref="SnapshotSerializer.ReturnPayloadBuffer"/>).
        /// </summary>
        public static void InsertOrReplaceByFrame(List<WorldSnapshot> list, WorldSnapshot entry)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].FixedFrame == entry.FixedFrame)
                {
                    list[i] = entry;
                    return;
                }
            }
            int insertAt = 0;
            while (insertAt < list.Count && list[insertAt].FixedFrame < entry.FixedFrame)
            {
                insertAt++;
            }
            list.Insert(insertAt, entry);
        }

        /// <summary>
        /// Copy <paramref name="src"/> into a fresh
        /// <see cref="IterableDictionary{TKey, TValue}"/>. Used to hand a
        /// snapshot of the recorder's per-frame checksums to a
        /// <see cref="RecordingBundle"/> without sharing the recorder's
        /// live dict (the recorder may keep mutating after Stop / Save).
        /// </summary>
        public static IterableDictionary<int, ulong> CopyChecksums(
            IterableDictionary<int, ulong> src
        )
        {
            var copy = new IterableDictionary<int, ulong>();
            foreach (var (frame, checksum) in src)
            {
                copy.Add(frame, checksum);
            }
            return copy;
        }
    }
}
