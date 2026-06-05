using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.UIElements;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Covers the hierarchy window's search engine (TrecsHierarchySearch):
    // tokenizing, smart-case, negation, the t: kind selector, per-kind
    // predicate dispatch, highlight-span building, and the search-active
    // flat-leaf harvest. All pure logic — no window or world required.
    [TestFixture]
    public class TrecsHierarchySearchTests
    {
        static TrecsHierarchySearch Engine(IAccessTracker tracker = null) => new(() => tracker);

        static TrecsHierarchySearch Parsed(string query, IAccessTracker tracker = null)
        {
            var e = Engine(tracker);
            e.Parse(query);
            return e;
        }

        static TemplateRef Template(
            string name,
            string[] tags = null,
            string[] components = null,
            string[] bases = null,
            string[] derived = null
        ) =>
            new(
                new TrecsSchemaTemplate
                {
                    DebugName = name,
                    IsResolved = true,
                    AllTagNames = new List<string>(tags ?? Array.Empty<string>()),
                    ComponentTypeNames = new List<string>(components ?? Array.Empty<string>()),
                    BaseTemplateNames = new List<string>(bases ?? Array.Empty<string>()),
                    DerivedTemplateNames = new List<string>(derived ?? Array.Empty<string>()),
                }
            );

        // ----- Tokenize --------------------------------------------------

        [Test]
        public void Tokenize_SplitsOnSpacesAndTabs()
        {
            NAssert.That(
                TrecsHierarchySearch.Tokenize("a b\tc"),
                Is.EqualTo(new[] { "a", "b", "c" })
            );
        }

        [Test]
        public void Tokenize_EmptyAndWhitespaceProduceNoTokens()
        {
            NAssert.That(TrecsHierarchySearch.Tokenize(""), Is.Empty);
            NAssert.That(TrecsHierarchySearch.Tokenize("   \t "), Is.Empty);
        }

        [Test]
        public void Tokenize_QuotedPhraseIsSingleTokenWithQuotesStripped()
        {
            NAssert.That(
                TrecsHierarchySearch.Tokenize("\"a b\" c"),
                Is.EqualTo(new[] { "a b", "c" })
            );
        }

        [Test]
        public void Tokenize_QuotesCanWrapPartOfAToken()
        {
            // Trailing space kept inside the quoted region: `id:"42 "`.
            NAssert.That(
                TrecsHierarchySearch.Tokenize("id:\"42 \""),
                Is.EqualTo(new[] { "id:42 " })
            );
        }

        [Test]
        public void Tokenize_UnterminatedQuoteRunsToEndOfInput()
        {
            NAssert.That(
                TrecsHierarchySearch.Tokenize("\"player spaw"),
                Is.EqualTo(new[] { "player spaw" })
            );
        }

        // ----- Smart-case ------------------------------------------------

        [Test]
        public void ComparisonForToken_LowercaseIsCaseInsensitive()
        {
            NAssert.That(
                TrecsHierarchySearch.ComparisonForToken("play"),
                Is.EqualTo(StringComparison.OrdinalIgnoreCase)
            );
        }

        [Test]
        public void ComparisonForToken_AnyUppercaseFlipsToCaseSensitive()
        {
            NAssert.That(
                TrecsHierarchySearch.ComparisonForToken("pLay"),
                Is.EqualTo(StringComparison.Ordinal)
            );
        }

        [Test]
        public void Matches_LowercaseTokenMatchesAnyCase()
        {
            var e = Parsed("play");
            NAssert.That(e.Matches(SearchScope.Templates, "PlayerSpawner"), Is.True);
        }

        [Test]
        public void Matches_UppercaseTokenRequiresExactCase()
        {
            var e = Parsed("Play");
            NAssert.That(e.Matches(SearchScope.Templates, "playerspawner"), Is.False);
            NAssert.That(e.Matches(SearchScope.Templates, "PlayerSpawner"), Is.True);
        }

        // ----- Bare substrings, negation, AND-ing -----------------------

        [Test]
        public void EmptyQuery_IsInactiveAndMatchesEverything()
        {
            var e = Parsed("");
            NAssert.That(e.IsActive, Is.False);
            NAssert.That(e.Matches(SearchScope.Templates, "Anything"), Is.True);
        }

        [Test]
        public void QuoteOnlyQueries_TokenizeToNothingAndStayInactive()
        {
            // The window keys flat-mode rendering on IsActive — a lone
            // opening quote (the start of a "quoted phrase") or empty
            // quotes must not count as an effective filter.
            NAssert.That(Parsed("\"").IsActive, Is.False);
            NAssert.That(Parsed("\"\"").IsActive, Is.False);
            NAssert.That(Parsed(" \"\" ").IsActive, Is.False);
        }

        [Test]
        public void Matches_MultipleBareTokensAndTogether()
        {
            var e = Parsed("cave bounds");
            NAssert.That(e.Matches(SearchScope.Templates, "Cave Bounds"), Is.True);
            NAssert.That(e.Matches(SearchScope.Templates, "Cave Wall"), Is.False);
        }

        [Test]
        public void Matches_NegatedBareTokenExcludes()
        {
            var e = Parsed("-boss");
            NAssert.That(e.Matches(SearchScope.Templates, "BigBoss"), Is.False);
            NAssert.That(e.Matches(SearchScope.Templates, "Minion"), Is.True);
        }

        [Test]
        public void Matches_AltNameCountsAsAMatchSurface()
        {
            var e = Parsed("cfoo");
            NAssert.That(
                e.Matches(SearchScope.Components, "Foo", "CFoo", in PredicateData.Empty),
                Is.True
            );
        }

        [Test]
        public void Matches_QuotedPhraseMatchesSubstringWithSpaces()
        {
            var e = Parsed("\"my long\"");
            NAssert.That(e.Matches(SearchScope.Templates, "My Long Name"), Is.True);
            NAssert.That(e.Matches(SearchScope.Templates, "MyLongName"), Is.False);
        }

        // ----- Kind selector ---------------------------------------------

        [Test]
        public void KindSelector_RestrictsScope()
        {
            var e = Parsed("t:e");
            NAssert.That(e.IsActive, Is.True);
            NAssert.That(e.Matches(SearchScope.Entities, "anything"), Is.True);
            NAssert.That(e.Matches(SearchScope.Templates, "anything"), Is.False);
        }

        [Test]
        public void KindSelector_LongFormsResolve()
        {
            NAssert.That(
                TrecsHierarchySearch.ScopeFromKindValue("templates"),
                Is.EqualTo(SearchScope.Templates)
            );
            NAssert.That(
                TrecsHierarchySearch.ScopeFromKindValue("tag"),
                Is.EqualTo(SearchScope.Tags)
            );
            NAssert.That(
                TrecsHierarchySearch.ScopeFromKindValue("bogus"),
                Is.EqualTo((SearchScope)0)
            );
        }

        [Test]
        public void KindSelector_UnknownValueFallsThroughToBareSubstring()
        {
            var e = Parsed("t:bogus");
            // The literal "t:bogus" becomes a substring requirement.
            NAssert.That(e.Matches(SearchScope.Templates, "xt:bogusy"), Is.True);
            NAssert.That(e.Matches(SearchScope.Templates, "Player"), Is.False);
        }

        [Test]
        public void KindSelector_NegatedFallsThroughToNegatedBareSubstring()
        {
            var e = Parsed("-t:e");
            NAssert.That(e.Matches(SearchScope.Templates, "Player"), Is.True);
            // Contains the literal "t:e" → excluded.
            NAssert.That(e.Matches(SearchScope.Templates, "set:entity"), Is.False);
        }

        [Test]
        public void UnknownPredicateKey_FallsThroughToBareSubstring()
        {
            var e = Parsed("foo:bar");
            NAssert.That(e.Matches(SearchScope.Templates, "xfoo:barx"), Is.True);
            NAssert.That(e.Matches(SearchScope.Templates, "foobar"), Is.False);
        }

        // ----- Template predicates ----------------------------------------

        [Test]
        public void TemplatePredicate_Tag()
        {
            var e = Parsed("tag:enem");
            var ctx = new PredicateData { Template = Template("Grunt", tags: new[] { "enemy" }) };
            NAssert.That(e.Matches(SearchScope.Templates, "Grunt", in ctx), Is.True);
            var noTag = new PredicateData { Template = Template("Crate") };
            NAssert.That(e.Matches(SearchScope.Templates, "Crate", in noTag), Is.False);
        }

        [Test]
        public void TemplatePredicate_ComponentBothSpellings()
        {
            var ctx = new PredicateData
            {
                Template = Template("Grunt", components: new[] { "Health" }),
            };
            NAssert.That(
                Parsed("c:health").Matches(SearchScope.Templates, "Grunt", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("component:health").Matches(SearchScope.Templates, "Grunt", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("c:mana").Matches(SearchScope.Templates, "Grunt", in ctx),
                Is.False
            );
        }

        [Test]
        public void TemplatePredicate_BaseAndDerived()
        {
            var ctx = new PredicateData
            {
                Template = Template(
                    "Boss",
                    bases: new[] { "Enemy" },
                    derived: new[] { "MegaBoss" }
                ),
            };
            NAssert.That(
                Parsed("base:enemy").Matches(SearchScope.Templates, "Boss", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("derived:mega").Matches(SearchScope.Templates, "Boss", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("base:crate").Matches(SearchScope.Templates, "Boss", in ctx),
                Is.False
            );
        }

        [Test]
        public void Predicate_NegationInverts()
        {
            var e = Parsed("-tag:enemy");
            var enemy = new PredicateData { Template = Template("Grunt", tags: new[] { "enemy" }) };
            var crate = new PredicateData { Template = Template("Crate") };
            NAssert.That(e.Matches(SearchScope.Templates, "Grunt", in enemy), Is.False);
            NAssert.That(e.Matches(SearchScope.Templates, "Crate", in crate), Is.True);
        }

        // ----- Entity predicates ------------------------------------------

        [Test]
        public void EntityPredicates_ReuseParentTemplateData()
        {
            var ctx = new PredicateData
            {
                Template = Template(
                    "Grunt",
                    tags: new[] { "enemy" },
                    components: new[] { "Health" }
                ),
            };
            NAssert.That(
                Parsed("tag:enemy").Matches(SearchScope.Entities, "Grunt #5", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("c:health").Matches(SearchScope.Entities, "Grunt #5", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("template:grun").Matches(SearchScope.Entities, "Grunt #5", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("template:crate").Matches(SearchScope.Entities, "Grunt #5", in ctx),
                Is.False
            );
        }

        // ----- Component / set / tag predicates ---------------------------

        [Test]
        public void ComponentPredicate_MatchesOwnDisplayNameOnly()
        {
            var ctx = new PredicateData
            {
                ComponentType = new ComponentTypeRef(typeof(int), "Health"),
            };
            NAssert.That(
                Parsed("c:heal").Matches(SearchScope.Components, "Health", in ctx),
                Is.True
            );
            // A component row isn't itself tagged — predicate filters it out.
            NAssert.That(
                Parsed("tag:enemy").Matches(SearchScope.Components, "Health", in ctx),
                Is.False
            );
        }

        [Test]
        public void SetPredicate_Tag()
        {
            var ctx = new PredicateData
            {
                Set = new SetRef(
                    new TrecsSchemaSet
                    {
                        DebugName = "Visible",
                        TagNames = new List<string> { "drawable" },
                    }
                ),
            };
            NAssert.That(Parsed("tag:draw").Matches(SearchScope.Sets, "Visible", in ctx), Is.True);
            NAssert.That(
                Parsed("tag:enemy").Matches(SearchScope.Sets, "Visible", in ctx),
                Is.False
            );
        }

        [Test]
        public void TagPredicate_MatchesItself()
        {
            var ctx = new PredicateData
            {
                Tag = new TagRef(new TrecsSchemaTag { Name = "Player" }),
            };
            NAssert.That(Parsed("tag:play").Matches(SearchScope.Tags, "Player", in ctx), Is.True);
            NAssert.That(Parsed("tag:enemy").Matches(SearchScope.Tags, "Player", in ctx), Is.False);
        }

        [Test]
        public void Predicate_OnHeaderScopeFiltersRowOut()
        {
            // Substring-only sites (sections, partitions, phases) use the
            // two-arg overload; an active predicate must filter them out.
            var e = Parsed("tag:player");
            NAssert.That(e.Matches(SearchScope.Partitions, "Group 1"), Is.False);
        }

        // ----- Accessor predicates -----------------------------------------

        sealed class FakeTracker : IAccessTracker
        {
            public readonly Dictionary<string, string[]> Reads = new();
            public readonly Dictionary<string, string[]> Writes = new();

            static readonly string[] None = Array.Empty<string>();

            static IReadOnlyCollection<string> Get(Dictionary<string, string[]> map, string key) =>
                map.TryGetValue(key, out var v) ? v : None;

            public IReadOnlyCollection<string> GetComponentsReadBy(string systemName) =>
                Get(Reads, systemName);

            public IReadOnlyCollection<string> GetComponentsWrittenBy(string systemName) =>
                Get(Writes, systemName);

            public IReadOnlyCollection<string> GetReadersOfComponent(string c) => None;

            public IReadOnlyCollection<string> GetWritersOfComponent(string c) => None;

            public IReadOnlyCollection<string> GetTagNamesTouchedBy(string a) => None;

            public IReadOnlyCollection<string> GetTemplateNamesAddedBy(string a) => None;

            public IReadOnlyCollection<string> GetTemplateNamesRemovedBy(string a) => None;

            public IReadOnlyCollection<string> GetTemplateNamesMovedBy(string a) => None;

            public IReadOnlyCollection<string> GetSystemsAddingTo(string t) => None;

            public IReadOnlyCollection<string> GetSystemsRemovingFrom(string t) => None;

            public IReadOnlyCollection<string> GetSystemsMovingOn(string t) => None;
        }

        [Test]
        public void AccessorPredicate_ReadsWritesAccesses()
        {
            var tracker = new FakeTracker();
            tracker.Reads["MoveSystem"] = new[] { "Position" };
            tracker.Writes["MoveSystem"] = new[] { "Velocity" };
            var ctx = new PredicateData { AccessorDebugName = "MoveSystem" };

            NAssert.That(
                Parsed("reads:pos", tracker).Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("reads:vel", tracker).Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.False
            );
            NAssert.That(
                Parsed("writes:vel", tracker).Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("accesses:pos", tracker)
                    .Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("accesses:vel", tracker)
                    .Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.True
            );
            NAssert.That(
                Parsed("accesses:health", tracker)
                    .Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.False
            );
        }

        [Test]
        public void AccessorPredicate_NoTrackerFiltersOut()
        {
            var ctx = new PredicateData { AccessorDebugName = "MoveSystem" };
            NAssert.That(
                Parsed("reads:pos", tracker: null)
                    .Matches(SearchScope.Accessors, "MoveSystem", in ctx),
                Is.False
            );
        }

        // ----- Highlight --------------------------------------------------

        [Test]
        public void Highlight_NoPositiveTokens_DeclinesPlain()
        {
            NAssert.That(Parsed("").TryBuildHighlightedRichText("Player", out _), Is.False);
            NAssert.That(Parsed("-x").TryBuildHighlightedRichText("Player", out _), Is.False);
            NAssert.That(Parsed("tag:x").TryBuildHighlightedRichText("Player", out _), Is.False);
        }

        [Test]
        public void Highlight_WrapsMatchPreservingOriginalCasing()
        {
            var ok = Parsed("play").TryBuildHighlightedRichText("PlayerSpawner", out var rich);
            NAssert.That(ok, Is.True);
            NAssert.That(rich, Is.EqualTo("<color=#FFD24A><b>Play</b></color>erSpawner"));
        }

        [Test]
        public void Highlight_GenericTypeNamesDecline()
        {
            NAssert.That(
                Parsed("foo").TryBuildHighlightedRichText("Interpolated<CFoo>", out _),
                Is.False
            );
        }

        [Test]
        public void Highlight_NoMatchInThisNameDeclines()
        {
            NAssert.That(Parsed("zzz").TryBuildHighlightedRichText("Player", out _), Is.False);
        }

        [Test]
        public void Highlight_OverlappingSpansMerge()
        {
            var ok = Parsed("abc bcd").TryBuildHighlightedRichText("xabcdy", out var rich);
            NAssert.That(ok, Is.True);
            NAssert.That(rich, Is.EqualTo("x<color=#FFD24A><b>abcd</b></color>y"));
        }

        [Test]
        public void Highlight_AdjacentSpansMerge()
        {
            var ok = Parsed("ab cd").TryBuildHighlightedRichText("abcd", out var rich);
            NAssert.That(ok, Is.True);
            NAssert.That(rich, Is.EqualTo("<color=#FFD24A><b>abcd</b></color>"));
        }

        [Test]
        public void Highlight_SeparatedOccurrencesEachWrapped()
        {
            var ok = Parsed("a").TryBuildHighlightedRichText("banana", out var rich);
            NAssert.That(ok, Is.True);
            NAssert.That(
                rich,
                Is.EqualTo(
                    "b<color=#FFD24A><b>a</b></color>n"
                        + "<color=#FFD24A><b>a</b></color>n"
                        + "<color=#FFD24A><b>a</b></color>"
                )
            );
        }

        [Test]
        public void Highlight_BackToBackOccurrencesMergeIntoOneSpan()
        {
            // "an" matches at 1 and 3 — the spans touch, so the adjacent-
            // merge rule folds them into a single wrap.
            var ok = Parsed("an").TryBuildHighlightedRichText("banana", out var rich);
            NAssert.That(ok, Is.True);
            NAssert.That(rich, Is.EqualTo("b<color=#FFD24A><b>anan</b></color>a"));
        }

        // ----- Flat-leaf harvest -------------------------------------------

        static TreeViewItemData<RowData> Row(
            int id,
            RowKind kind,
            string name,
            List<TreeViewItemData<RowData>> children = null
        ) => new(id, new RowData { Kind = kind, DisplayName = name }, children);

        static List<TreeViewItemData<RowData>> SampleSectionChildren() =>
            new()
            {
                Row(
                    100,
                    RowKind.Template,
                    "Grunt",
                    new List<TreeViewItemData<RowData>>
                    {
                        Row(101, RowKind.Entity, "Grunt #1"),
                        Row(102, RowKind.MorePlaceholder, "… 3 more not shown"),
                    }
                ),
                Row(103, RowKind.ComponentType, "Health"),
            };

        [Test]
        public void Harvest_DropsHeadersAndFlattensLeaves()
        {
            var sink = new List<TreeViewItemData<RowData>>();
            Parsed("x").HarvestFlatLeaves(SampleSectionChildren(), sink);
            var names = sink.ConvertAll(i => i.data.DisplayName);
            NAssert.That(names, Is.EquivalentTo(new[] { "Grunt", "Grunt #1", "Health" }));
            // Harvested leaves carry no children.
            NAssert.That(sink.TrueForAll(i => !i.hasChildren), Is.True);
        }

        [Test]
        public void Harvest_ReappliesExplicitKindMask()
        {
            // In hierarchy mode the template parent survives because its
            // entity child matched; flat mode re-checks the kind mask and
            // drops it.
            var sink = new List<TreeViewItemData<RowData>>();
            Parsed("t:e grunt").HarvestFlatLeaves(SampleSectionChildren(), sink);
            var names = sink.ConvertAll(i => i.data.DisplayName);
            NAssert.That(names, Is.EqualTo(new[] { "Grunt #1" }));
        }
    }
}
