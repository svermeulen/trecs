using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;

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

    /// <summary>
    /// The hierarchy window's search engine: parses the search field's
    /// query syntax into a <see cref="ParsedSearch"/> and evaluates it
    /// against rows. Owns tokenizing (quoted phrases), smart-case, '-'
    /// negation, the <c>t:</c> kind selector, per-kind predicate dispatch
    /// (<c>tag:</c>, <c>c:</c>, <c>base:</c>, <c>reads:</c>, …), the
    /// search-active flat-leaf harvest, and match-span highlighting.
    /// Extracted from <see cref="TrecsHierarchyWindow"/> so the query
    /// semantics are unit-testable without an open window — the window
    /// keeps only the UI wiring (field callbacks, label application).
    /// </summary>
    sealed class TrecsHierarchySearch
    {
        readonly ParsedSearch _filter = new();

        // Resolved lazily per accessor-predicate evaluation — the window
        // recreates its schema source on every structural rebuild, so the
        // engine can't capture a tracker instance at construction.
        readonly Func<IAccessTracker> _accessTracker;

        public TrecsHierarchySearch(Func<IAccessTracker> accessTracker)
        {
            _accessTracker = accessTracker;
        }

        // True iff the user has typed something — either text, a kind
        // selector, or a predicate. Filter sites short-circuit when this
        // is false to keep the no-filter rebuild path cheap.
        public bool IsActive => !_filter.IsEmpty;

        // Tokenizes the input on whitespace, respecting double-quoted
        // phrases as single tokens, and bins each one of:
        //   - "t:value" — kind selector
        //   - "key:value" with a recognized predicate key — typed predicate
        //   - bare word OR "key:value" with an unrecognized key — bare
        //     substring (so accidental colons don't silently disappear)
        //
        // A leading '-' on a token negates it: -tag:player excludes rows
        // tagged player; -foo excludes rows whose display name contains
        // foo. The kind selector ("t:") doesn't negate — "-t:e" falls
        // through to bare substring.
        public void Parse(string raw)
        {
            _filter.Reset();
            raw ??= string.Empty;
            foreach (var rawTok in Tokenize(raw))
            {
                if (rawTok.Length == 0)
                    continue;
                bool negate = false;
                var tok = rawTok;
                if (tok.Length > 1 && tok[0] == '-')
                {
                    negate = true;
                    tok = tok.Substring(1);
                }
                int colon = tok.IndexOf(':');
                if (colon <= 0)
                {
                    _filter.BareSubstrings.Add((tok, negate));
                    continue;
                }
                var key = tok.Substring(0, colon).ToLowerInvariant();
                var value = tok.Substring(colon + 1);
                if (key == "t" && !negate)
                {
                    var kind = ScopeFromKindValue(value);
                    if (kind != 0)
                    {
                        _filter.ExplicitKind = kind;
                        _filter.HasExplicitKind = true;
                    }
                    else
                    {
                        _filter.BareSubstrings.Add((tok, false));
                    }
                    continue;
                }
                if (IsKnownPredicate(key))
                {
                    _filter.Predicates.Add((key, value, negate));
                }
                else
                {
                    _filter.BareSubstrings.Add((tok, negate));
                }
            }
        }

        // Whitespace-tokenizer that treats "..." as a single token (the
        // quotes are stripped). Used so the user can search for substrings
        // containing spaces, e.g. "Player Spawner" or "id:42 ".
        internal static List<string> Tokenize(string raw)
        {
            var tokens = new List<string>();
            var cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (!inQuote && (c == ' ' || c == '\t'))
                {
                    if (cur.Length > 0)
                    {
                        tokens.Add(cur.ToString());
                        cur.Clear();
                    }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0)
                tokens.Add(cur.ToString());
            return tokens;
        }

        // Smart-case: a bare substring with no uppercase characters
        // matches case-insensitively (the typical fast-typing case),
        // while introducing any uppercase character flips the token to
        // case-sensitive — same convention as ripgrep, vim, ag, etc.
        // Picked per token, so `play COLL` requires a literal "COLL"
        // even though `play` still matches loosely.
        internal static StringComparison ComparisonForToken(string tok)
        {
            if (string.IsNullOrEmpty(tok))
                return StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < tok.Length; i++)
            {
                if (char.IsUpper(tok[i]))
                    return StringComparison.Ordinal;
            }
            return StringComparison.OrdinalIgnoreCase;
        }

        internal static SearchScope ScopeFromKindValue(string v) =>
            (v ?? "").ToLowerInvariant() switch
            {
                "e" or "entity" or "entities" => SearchScope.Entities,
                "t" or "template" or "templates" => SearchScope.Templates,
                "c" or "component" or "components" => SearchScope.Components,
                "s" or "set" or "sets" => SearchScope.Sets,
                "tag" or "tags" => SearchScope.Tags,
                "a" or "accessor" or "accessors" => SearchScope.Accessors,
                _ => 0,
            };

        internal static bool IsKnownPredicate(string key) =>
            key switch
            {
                "tag"
                or "c"
                or "component"
                or "base"
                or "derived"
                or "template"
                or "reads"
                or "writes"
                or "accesses" => true,
                _ => false,
            };

        // Predicate-aware row match. Returns true when the row satisfies
        // every part of the active filter:
        //   - Kind: row's scope must overlap the explicit t: selector
        //     (or no selector → any kind ok).
        //   - Bare substrings: each must appear in displayName.
        //   - Predicates: each key must be defined for this kind, and at
        //     least one of the kind's resolved values for that key must
        //     contain the user's value as a substring.
        // PredicateData is passed by ref so callers can stamp only the
        // fields relevant to the current row's kind.
        public bool Matches(
            SearchScope rowScope,
            string displayName,
            string altName,
            in PredicateData ctx
        )
        {
            var f = _filter;
            if (f.IsEmpty)
                return true;
            if (f.HasExplicitKind && (f.ExplicitKind & rowScope) == 0)
                return false;
            for (int i = 0; i < f.BareSubstrings.Count; i++)
            {
                var (sub, negate) = f.BareSubstrings[i];
                var cmp = ComparisonForToken(sub);
                bool inDisplay = displayName != null && displayName.IndexOf(sub, cmp) >= 0;
                bool inAlt = altName != null && altName.IndexOf(sub, cmp) >= 0;
                bool match = inDisplay || inAlt;
                if (negate ? match : !match)
                    return false;
            }
            for (int i = 0; i < f.Predicates.Count; i++)
            {
                var (key, value, negate) = f.Predicates[i];
                bool match = MatchesPredicate(rowScope, key, value, in ctx);
                if (negate ? match : !match)
                    return false;
            }
            return true;
        }

        public bool Matches(SearchScope rowScope, string displayName, in PredicateData ctx) =>
            Matches(rowScope, displayName, null, in ctx);

        // Substring-only sites (sections, partition rows, phase headings).
        // A query that includes predicates filters these out — predicate
        // dispatch returns false for kinds it doesn't handle, which is the
        // desired behavior (predicates need to apply somewhere, and
        // partitions etc. aren't a meaningful target).
        public bool Matches(SearchScope rowScope, string displayName) =>
            Matches(rowScope, displayName, null, in PredicateData.Empty);

        // While search is active the tree renders a flat list of matching
        // leaves instead of the section hierarchy. Walks the per-section
        // subtree and pulls out every content row, dropping headers
        // (Section, AccessorPhase, Group, MorePlaceholder). Each harvested
        // leaf is reconstructed without children so it sits at the root of
        // the flat tree.
        //
        // The explicit-kind filter (e.g. `t:e`) gets re-applied here: the
        // tree build keeps a template node when any of its entity children
        // match, since hierarchy-mode rendering needs the template as the
        // structural parent of those children. In flat mode there's no
        // structural reason to keep it, so a `t:e` search would otherwise
        // show both the template row and its matching entities.
        // Re-checking the kind mask drops the template.
        public void HarvestFlatLeaves(
            List<TreeViewItemData<RowData>> source,
            List<TreeViewItemData<RowData>> sink
        )
        {
            var kindMask = _filter.HasExplicitKind ? _filter.ExplicitKind : SearchScope.All;
            HarvestFlatLeavesInner(source, sink, kindMask);
        }

        static void HarvestFlatLeavesInner(
            List<TreeViewItemData<RowData>> source,
            List<TreeViewItemData<RowData>> sink,
            SearchScope kindMask
        )
        {
            foreach (var item in source)
            {
                var kind = item.data.Kind;
                var rowScope = ScopeForRowKind(kind);
                bool isLeafContent = rowScope != 0;
                if (isLeafContent && (rowScope & kindMask) != 0)
                {
                    sink.Add(new TreeViewItemData<RowData>(item.id, item.data));
                }
                if (item.hasChildren)
                {
                    HarvestFlatLeavesInner(
                        (List<TreeViewItemData<RowData>>)item.children,
                        sink,
                        kindMask
                    );
                }
            }
        }

        // Maps a row's RowKind to the SearchScope it represents. Header
        // kinds (Section / AccessorPhase / Group / MorePlaceholder) return
        // 0 since they aren't user-searchable content.
        internal static SearchScope ScopeForRowKind(RowKind kind) =>
            kind switch
            {
                RowKind.Template or RowKind.AbstractTemplate => SearchScope.Templates,
                RowKind.Entity => SearchScope.Entities,
                RowKind.Accessor => SearchScope.Accessors,
                RowKind.ComponentType => SearchScope.Components,
                RowKind.SetItem => SearchScope.Sets,
                RowKind.TagItem => SearchScope.Tags,
                _ => 0,
            };

        // Builds the row text with every non-negated bare search substring
        // wrapped in a bold yellow rich-text span, merging overlapping /
        // adjacent match spans so <color> tags never nest. Returns false
        // when the label should render plain: no positive bare tokens, a
        // generic-type display name (literal "<>" would be mangled by the
        // rich-text parser), or no match in this name.
        public bool TryBuildHighlightedRichText(string displayName, out string richText)
        {
            richText = null;
            if (displayName == null)
                return false;
            // Fast path: no positive bare tokens → nothing to highlight.
            // The tree binds every visible row on every refresh, so
            // skipping the IndexOf scans when there's nothing to highlight
            // is worth a branch.
            var bare = _filter.BareSubstrings;
            bool anyPositive = false;
            for (int i = 0; i < bare.Count; i++)
            {
                if (!bare[i].Negate && !string.IsNullOrEmpty(bare[i].Substring))
                {
                    anyPositive = true;
                    break;
                }
            }
            if (!anyPositive)
                return false;
            // Generic-type display names contain literal "<>" — bypass
            // rich text so Unity's parser doesn't mangle them. Those
            // rows still render, just without a highlight band.
            if (displayName.IndexOf('<') >= 0)
                return false;

            // Collect every match span across all positive substrings,
            // then merge overlapping/adjacent spans so the rich-text
            // wrapping doesn't nest <color> tags or split the same
            // character across multiple spans.
            var spans = new List<(int start, int end)>();
            for (int i = 0; i < bare.Count; i++)
            {
                var (sub, neg) = bare[i];
                if (neg || string.IsNullOrEmpty(sub))
                    continue;
                var cmp = ComparisonForToken(sub);
                int from = 0;
                while (from <= displayName.Length - sub.Length)
                {
                    int idx = displayName.IndexOf(sub, from, cmp);
                    if (idx < 0)
                        break;
                    spans.Add((idx, idx + sub.Length));
                    from = idx + sub.Length;
                }
            }
            if (spans.Count == 0)
                return false;
            spans.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)> { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                var top = merged[merged.Count - 1];
                var s = spans[i];
                if (s.start <= top.end)
                {
                    merged[merged.Count - 1] = (top.start, Math.Max(top.end, s.end));
                }
                else
                {
                    merged.Add(s);
                }
            }

            var sb = new StringBuilder(displayName.Length + merged.Count * 24);
            int cursor = 0;
            foreach (var (start, end) in merged)
            {
                if (start > cursor)
                    sb.Append(displayName, cursor, start - cursor);
                sb.Append("<color=#FFD24A><b>");
                sb.Append(displayName, start, end - start);
                sb.Append("</b></color>");
                cursor = end;
            }
            if (cursor < displayName.Length)
                sb.Append(displayName, cursor, displayName.Length - cursor);
            richText = sb.ToString();
            return true;
        }

        // Dispatch table: returns true if at least one of the row's
        // resolved values for `key` contains `value` as a substring. False
        // if the predicate isn't defined for this kind (so the row gets
        // filtered) or no value matches.
        bool MatchesPredicate(SearchScope rowScope, string key, string value, in PredicateData ctx)
        {
            switch (rowScope)
            {
                case SearchScope.Templates:
                    return MatchesTemplatePredicate(key, value, in ctx);
                case SearchScope.Entities:
                    return MatchesEntityPredicate(key, value, in ctx);
                case SearchScope.Components:
                    return MatchesComponentPredicate(key, value, in ctx);
                case SearchScope.Sets:
                    return MatchesSetPredicate(key, value, in ctx);
                case SearchScope.Tags:
                    return MatchesTagPredicate(key, value, in ctx);
                case SearchScope.Accessors:
                    return MatchesAccessorPredicate(key, value, in ctx);
                default:
                    return false;
            }
        }

        static bool MatchesTemplatePredicate(string key, string value, in PredicateData ctx)
        {
            var t = ctx.Template;
            if (t == null)
                return false;
            return key switch
            {
                "tag" => AnyContains(t.AllTagNames, value),
                "c" or "component" => AnyContains(t.ComponentTypeNames, value),
                "base" => AnyContains(t.BaseTemplateNames, value),
                "derived" => AnyContains(t.DerivedTemplateNames, value),
                _ => false,
            };
        }

        static bool MatchesEntityPredicate(string key, string value, in PredicateData ctx)
        {
            // Entity rows reuse their parent template's data for tag/component
            // queries — entities don't carry per-instance tags or components,
            // they inherit from the resolved template they were spawned from.
            switch (key)
            {
                case "tag":
                case "c":
                case "component":
                    return MatchesTemplatePredicate(key, value, in ctx);
                case "template":
                    var n = ctx.Template?.DebugName;
                    return n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    return false;
            }
        }

        static bool MatchesComponentPredicate(string key, string value, in PredicateData ctx)
        {
            // Components only answer "c:X" — matches when the row's own
            // display name contains X. Other predicates filter the row out
            // (a component isn't itself tagged, doesn't have a base, etc.).
            if (key != "c" && key != "component")
                return false;
            var name = ctx.ComponentType?.DisplayName;
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool MatchesSetPredicate(string key, string value, in PredicateData ctx)
        {
            if (key != "tag" || ctx.Set == null)
                return false;
            return AnyContains(ctx.Set.TagNames, value);
        }

        static bool MatchesTagPredicate(string key, string value, in PredicateData ctx)
        {
            // A tag "has" itself — so tag:player matches the tag named
            // player. Other predicates filter out (tags don't have
            // components/templates/etc.).
            if (key != "tag")
                return false;
            var name = ctx.Tag?.Name;
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool MatchesAccessorPredicate(string key, string value, in PredicateData ctx)
        {
            if (string.IsNullOrEmpty(ctx.AccessorDebugName))
            {
                return false;
            }
            var tracker = _accessTracker?.Invoke();
            if (tracker == null)
            {
                return false;
            }
            switch (key)
            {
                case "reads":
                    return AnyContains(tracker.GetComponentsReadBy(ctx.AccessorDebugName), value);
                case "writes":
                    return AnyContains(
                        tracker.GetComponentsWrittenBy(ctx.AccessorDebugName),
                        value
                    );
                case "accesses":
                    return AnyContains(tracker.GetComponentsReadBy(ctx.AccessorDebugName), value)
                        || AnyContains(
                            tracker.GetComponentsWrittenBy(ctx.AccessorDebugName),
                            value
                        );
                default:
                    return false;
            }
        }

        static bool AnyContains(IReadOnlyCollection<string> names, string value)
        {
            if (names == null || names.Count == 0)
                return false;
            foreach (var n in names)
            {
                if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
