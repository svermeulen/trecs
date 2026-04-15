using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs.Internal
{
    internal static class TagSetRegistry
    {
        static readonly Dictionary<int, IReadOnlyList<Tag>> _tagSetTags;
        static readonly Dictionary<int, string> _tagSetStrings;

        static int[] _guidBuffer = new int[16];

        static TagSetRegistry()
        {
            _tagSetTags = new() { { TagSet.Null.Id, new List<Tag>() } };

            _tagSetStrings = new() { { TagSet.Null.Id, "Null" } };
        }

        public static TagSet TagsToTagSet(IReadOnlyList<Tag> tags)
        {
            Assert.That(UnityThreadUtil.IsMainThread);

            if (tags.Count == 0)
            {
                return TagSet.Null;
            }

            int id = 0;

            for (int i = 0; i < tags.Count; i++)
            {
                id ^= tags[i].Guid;
            }

            if (id == 0)
            {
                id = 1;
            }

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                SortGuidsToBuffer(tags);
                AssertTagGuidsMatch(
                    id,
                    existing,
                    new ReadOnlySpan<int>(_guidBuffer, 0, tags.Count)
                );
#endif
                return new TagSet(id);
            }

            SortGuidsToBuffer(tags);
            var sortedTags = new Tag[tags.Count];

            for (int i = 0; i < tags.Count; i++)
            {
                sortedTags[i] = new Tag(_guidBuffer[i]);
            }

            InitTagSetTags(id, sortedTags);
            return new TagSet(id);
        }

        public static TagSet TagsToTagSet(Tag t1)
        {
            Assert.That(UnityThreadUtil.IsMainThread);

            int id = t1.Guid;

            if (id == 0)
            {
                id = 1;
            }

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                Span<int> guids = stackalloc int[] { t1.Guid };
                AssertTagGuidsMatch(id, existing, guids);
#endif
            }
            else
            {
                InitTagSetTags(id, new[] { t1 });
            }

            return new TagSet(id);
        }

        public static TagSet TagsToTagSet(Tag t1, Tag t2)
        {
            Assert.That(UnityThreadUtil.IsMainThread);

            int id = t1.Guid ^ t2.Guid;

            if (id == 0)
            {
                id = 1;
            }

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (t1.Guid > t2.Guid)
                    (t1, t2) = (t2, t1);
                Span<int> guids = stackalloc int[] { t1.Guid, t2.Guid };
                AssertTagGuidsMatch(id, existing, guids);
#endif
                return new TagSet(id);
            }

            if (t1.Guid > t2.Guid)
                (t1, t2) = (t2, t1);
            InitTagSetTags(id, new[] { t1, t2 });
            return new TagSet(id);
        }

        public static TagSet TagsToTagSet(Tag t1, Tag t2, Tag t3)
        {
            Assert.That(UnityThreadUtil.IsMainThread);

            int id = t1.Guid ^ t2.Guid ^ t3.Guid;

            if (id == 0)
            {
                id = 1;
            }

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (t1.Guid > t2.Guid)
                    (t1, t2) = (t2, t1);
                if (t2.Guid > t3.Guid)
                    (t2, t3) = (t3, t2);
                if (t1.Guid > t2.Guid)
                    (t1, t2) = (t2, t1);
                Span<int> guids = stackalloc int[] { t1.Guid, t2.Guid, t3.Guid };
                AssertTagGuidsMatch(id, existing, guids);
#endif
                return new TagSet(id);
            }

            if (t1.Guid > t2.Guid)
                (t1, t2) = (t2, t1);
            if (t2.Guid > t3.Guid)
                (t2, t3) = (t3, t2);
            if (t1.Guid > t2.Guid)
                (t1, t2) = (t2, t1);
            InitTagSetTags(id, new[] { t1, t2, t3 });
            return new TagSet(id);
        }

        public static TagSet TagsToTagSet(Tag t1, Tag t2, Tag t3, Tag t4)
        {
            Assert.That(UnityThreadUtil.IsMainThread);
            int id = t1.Guid ^ t2.Guid ^ t3.Guid ^ t4.Guid;

            if (id == 0)
            {
                id = 1;
            }

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (t1.Guid > t2.Guid)
                    (t1, t2) = (t2, t1);
                if (t3.Guid > t4.Guid)
                    (t3, t4) = (t4, t3);
                if (t1.Guid > t3.Guid)
                    (t1, t3) = (t3, t1);
                if (t2.Guid > t4.Guid)
                    (t2, t4) = (t4, t2);
                if (t2.Guid > t3.Guid)
                    (t2, t3) = (t3, t2);
                Span<int> guids = stackalloc int[] { t1.Guid, t2.Guid, t3.Guid, t4.Guid };
                AssertTagGuidsMatch(id, existing, guids);
#endif
                return new TagSet(id);
            }

            if (t1.Guid > t2.Guid)
                (t1, t2) = (t2, t1);
            if (t3.Guid > t4.Guid)
                (t3, t4) = (t4, t3);
            if (t1.Guid > t3.Guid)
                (t1, t3) = (t3, t1);
            if (t2.Guid > t4.Guid)
                (t2, t4) = (t4, t2);
            if (t2.Guid > t3.Guid)
                (t2, t3) = (t3, t2);
            InitTagSetTags(id, new[] { t1, t2, t3, t4 });
            return new TagSet(id);
        }

        public static TagSet CombineTagSets(TagSet a, TagSet b)
        {
            Assert.That(UnityThreadUtil.IsMainThread);

            var tagsA = TagSetToTags(a);
            var tagsB = TagSetToTags(b);

            // Compute XOR hash with dedup (both inputs already sorted by Guid)
            int ia = 0,
                ib = 0,
                hash = 0;
            while (ia < tagsA.Count && ib < tagsB.Count)
            {
                int ga = tagsA[ia].Guid;
                int gb = tagsB[ib].Guid;

                if (ga < gb)
                {
                    hash ^= ga;
                    ia++;
                }
                else if (ga > gb)
                {
                    hash ^= gb;
                    ib++;
                }
                else
                {
                    hash ^= ga;
                    ia++;
                    ib++;
                }
            }
            while (ia < tagsA.Count)
                hash ^= tagsA[ia++].Guid;
            while (ib < tagsB.Count)
                hash ^= tagsB[ib++].Guid;

            if (hash == 0)
            {
                hash = 1;
            }

            int id = hash;

            if (_tagSetTags.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                var mergedGuids = MergeSortedTagGuids(tagsA, tagsB);
                AssertTagGuidsMatch(id, existing, mergedGuids);
#endif
                return new TagSet(id);
            }

            var sortedTags = MergeSortedTags(tagsA, tagsB);
            InitTagSetTags(id, sortedTags);
            return new TagSet(id);
        }

        static Tag[] MergeSortedTags(IReadOnlyList<Tag> tagsA, IReadOnlyList<Tag> tagsB)
        {
            int totalMax = tagsA.Count + tagsB.Count;
            if (_guidBuffer.Length < totalMax)
                _guidBuffer = new int[totalMax];

            int ia = 0,
                ib = 0,
                count = 0;
            while (ia < tagsA.Count && ib < tagsB.Count)
            {
                int ga = tagsA[ia].Guid;
                int gb = tagsB[ib].Guid;

                if (ga < gb)
                {
                    _guidBuffer[count++] = ga;
                    ia++;
                }
                else if (ga > gb)
                {
                    _guidBuffer[count++] = gb;
                    ib++;
                }
                else
                {
                    _guidBuffer[count++] = ga;
                    ia++;
                    ib++;
                }
            }
            while (ia < tagsA.Count)
                _guidBuffer[count++] = tagsA[ia++].Guid;
            while (ib < tagsB.Count)
                _guidBuffer[count++] = tagsB[ib++].Guid;

            var result = new Tag[count];
            for (int i = 0; i < count; i++)
                result[i] = new Tag(_guidBuffer[i]);
            return result;
        }

#if TRECS_INTERNAL_CHECKS && DEBUG
        static int[] MergeSortedTagGuids(IReadOnlyList<Tag> tagsA, IReadOnlyList<Tag> tagsB)
        {
            int totalMax = tagsA.Count + tagsB.Count;
            if (_guidBuffer.Length < totalMax)
                _guidBuffer = new int[totalMax];

            int ia = 0,
                ib = 0,
                count = 0;
            while (ia < tagsA.Count && ib < tagsB.Count)
            {
                int ga = tagsA[ia].Guid;
                int gb = tagsB[ib].Guid;

                if (ga < gb)
                {
                    _guidBuffer[count++] = ga;
                    ia++;
                }
                else if (ga > gb)
                {
                    _guidBuffer[count++] = gb;
                    ib++;
                }
                else
                {
                    _guidBuffer[count++] = ga;
                    ia++;
                    ib++;
                }
            }
            while (ia < tagsA.Count)
                _guidBuffer[count++] = tagsA[ia++].Guid;
            while (ib < tagsB.Count)
                _guidBuffer[count++] = tagsB[ib++].Guid;

            var result = new int[count];
            Array.Copy(_guidBuffer, result, count);
            return result;
        }
#endif

        public static IReadOnlyList<Tag> TagSetToTags(TagSet tagSet)
        {
            return _tagSetTags[tagSet.Id];
        }

        public static string TagSetToString(TagSet tagSet)
        {
            if (!_tagSetStrings.TryGetValue(tagSet.Id, out var str))
            {
                str = string.Format(
                    "{0} ({1})",
                    TagSetToTags(tagSet).Select(x => x.ToString()).OrderBy(x => x).Join("-"),
                    tagSet.Id
                );

                _tagSetStrings.Add(tagSet.Id, str);
            }

            return str;
        }

        static void SortGuidsToBuffer(IReadOnlyList<Tag> tags)
        {
            if (_guidBuffer.Length < tags.Count)
            {
                _guidBuffer = new int[tags.Count];
            }

            for (int i = 0; i < tags.Count; i++)
            {
                _guidBuffer[i] = tags[i].Guid;
            }

            Array.Sort(_guidBuffer, 0, tags.Count);
        }

        // Callers must pass tags already sorted by Guid and also pass ownership
        static void InitTagSetTags(int id, IReadOnlyList<Tag> sortedTags)
        {
            for (int i = 1; i < sortedTags.Count; i++)
            {
                Assert.That(
                    sortedTags[i].Guid != sortedTags[i - 1].Guid,
                    "Duplicate tag in given tag set: {}",
                    sortedTags[i]
                );
            }
            _tagSetTags.Add(id, sortedTags);
        }

#if TRECS_INTERNAL_CHECKS && DEBUG
        static void AssertTagGuidsMatch(
            int id,
            IReadOnlyList<Tag> existing,
            ReadOnlySpan<int> sortedGuids
        )
        {
            if (existing.Count == sortedGuids.Length)
            {
                bool match = true;

                for (int i = 0; i < existing.Count; i++)
                {
                    if (existing[i].Guid != sortedGuids[i])
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
                $"TagSet hash collision! ID {id} maps to "
                    + $"[{string.Join(", ", existing.Select(t => t.ToString()))}] "
                    + $"but was requested with different tags"
            );
        }
#endif
    }
}
