using System;
using System.Linq;
using Trecs.Internal;
#if DEBUG && !TRECS_IS_PROFILING
using System.Collections.Generic;
#endif

namespace Trecs
{
    /// <summary>
    /// Creates <see cref="Tag"/> instances from <see cref="ITag"/> types. Derives the tag's
    /// stable integer ID from a <see cref="TagIdAttribute"/> if present, otherwise from a
    /// deterministic hash of the type's full name. Detects ID collisions in debug builds.
    /// </summary>
    public static class TagFactory
    {
#if DEBUG && !TRECS_IS_PROFILING
        // Track all created tag IDs to detect hash collisions
        static readonly Dictionary<int, Type> _registeredTagIds = new();
#endif

        public static Tag CreateTag(Type tagType)
        {
            TrecsAssert.That(UnityThreadHelper.IsMainThread);
            TrecsAssert.That(tagType.DerivesFrom(typeof(ITag)));

            int tagId;

            if (
                tagType.GetCustomAttributes(typeof(TagIdAttribute), false).FirstOrDefault()
                is TagIdAttribute idAttr
            )
            {
                tagId = idAttr.Id;
            }
            else
            {
                tagId = TypeToId(tagType);
            }

            TrecsAssert.That(tagId != 0);

#if DEBUG && !TRECS_IS_PROFILING
            if (_registeredTagIds.TryGetValue(tagId, out var existingType))
            {
                TrecsAssert.That(
                    existingType == tagType,
                    "Tag ID collision: {0} and {1} both resolve to ID {2}. Use [TagId] to assign explicit IDs.",
                    tagType.FullName,
                    existingType.FullName,
                    tagId
                );
            }
            else
            {
                _registeredTagIds.Add(tagId, tagType);
            }
#endif

            // We use FullName to generate hash but Name for debug name since
            // it's a lot more readable, especially for TagSet.ToString
            return new Tag(tagId, tagType.Name);
        }

        static int TypeToId(Type tagType)
        {
            var id = DenseHashUtil.StableStringHash(tagType.FullName);

            if (id == 0)
            {
                id = 1; // Reserve 0 for null
            }

            return id;
        }
    }
}
