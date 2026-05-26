using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Trecs.Internal
{
    /// <summary>
    /// Lazy <see cref="Tag"/>[] view + display-name string over the shared
    /// <see cref="TypeIdSet"/> intern table. <see cref="TagSet"/> stores its members as
    /// <see cref="TypeId"/>s (same int as <see cref="Tag.Value"/>); this cache produces
    /// an <see cref="IReadOnlyList{Tag}"/> on first <c>GetTags</c> call (for callers that
    /// want the typed view) and a Tag-name-joined string on first <c>GetDisplayString</c>
    /// call (for error messages and logging that should show <c>McAlive-McDead (id)</c>
    /// rather than <c>123-456 (id)</c>). Both are cached.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class TagSetCache
    {
        static readonly Dictionary<int, Tag[]> _tagsBySetId = new(
            new Dictionary<int, Tag[]> { { TypeIdSet.Null.Id, Array.Empty<Tag>() } }
        );

        static readonly Dictionary<int, string> _displayStringsBySetId = new(
            new Dictionary<int, string> { { TypeIdSet.Null.Id, "Null" } }
        );

        public static IReadOnlyList<Tag> GetTags(TypeIdSet set)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            if (_tagsBySetId.TryGetValue(set.Id, out var cached))
            {
                return cached;
            }

            var members = set.Members;
            var tags = new Tag[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                tags[i] = new Tag(members[i].Value);
            }
            _tagsBySetId.Add(set.Id, tags);
            return tags;
        }

        // Format: "TagA-TagB (id)" — same shape TagSetRegistry produced pre-unification,
        // preserved here because error messages (e.g. ambiguous-group diagnostics) match
        // on tag names rather than int hashes.
        public static string GetDisplayString(TypeIdSet set)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            if (_displayStringsBySetId.TryGetValue(set.Id, out var cached))
            {
                return cached;
            }

            var tags = GetTags(set);
            var result = string.Format(
                "{0} ({1})",
                tags.Select(t => t.ToString()).OrderBy(s => s).Join("-"),
                set.Id
            );
            _displayStringsBySetId.Add(set.Id, result);
            return result;
        }
    }
}
