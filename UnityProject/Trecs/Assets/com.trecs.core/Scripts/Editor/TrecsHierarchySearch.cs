using System;
using System.Collections.Generic;

namespace Trecs
{
    // Scope flags for the search field's prefix syntax. Bit per kind so
    // a row can ask "am I in scope?" with one mask AND. All has every
    // bit set; rows of any kind pass an All-scoped filter, but a
    // scoped filter (e.g. Templates) only lets matching rows through.
    // Partitions has its own bit so partition rows don't slip through
    // a scoped filter via a substring match on their tag-set display
    // string.
    [Flags]
    enum SearchScope
    {
        Templates = 1 << 0,
        Entities = 1 << 1,
        Components = 1 << 2,
        Accessors = 1 << 3,
        Sets = 1 << 4,
        Tags = 1 << 5,
        Partitions = 1 << 6,
        All = Templates | Entities | Components | Accessors | Sets | Tags | Partitions,
    }

    // Parsed form of the search field. Reused across keystrokes (Reset
    // clears the lists) so we don't allocate a new instance per change.
    sealed class ParsedSearch
    {
        public SearchScope ExplicitKind = SearchScope.All;
        public bool HasExplicitKind;

        // Negate is true when the user prefixed the token with '-'.
        // The matcher inverts the per-token result: a negated token
        // passes when the row would NOT have matched it.
        public readonly List<(string Key, string Value, bool Negate)> Predicates = new();
        public readonly List<(string Substring, bool Negate)> BareSubstrings = new();

        public bool IsEmpty =>
            !HasExplicitKind && Predicates.Count == 0 && BareSubstrings.Count == 0;

        public void Reset()
        {
            ExplicitKind = SearchScope.All;
            HasExplicitKind = false;
            Predicates.Clear();
            BareSubstrings.Clear();
        }
    }

    // Per-row data the predicate dispatch reads. Each row-build site
    // stamps the ref(s) its kind cares about and leaves the rest at
    // default. Passed by ref so the struct doesn't get copied per
    // predicate evaluation. Refs carry pre-projected name lists (tag
    // names, component names, etc.) populated by the source, so the
    // matchers don't have to branch on live-vs-cache.
    struct PredicateData
    {
        public static readonly PredicateData Empty = default;

        public TemplateRef Template; // for templates + entity rows
        public ComponentTypeRef ComponentType; // for component rows
        public SetRef Set; // for set rows
        public TagRef Tag; // for tag rows
        public string AccessorDebugName; // for accessor rows
    }
}
