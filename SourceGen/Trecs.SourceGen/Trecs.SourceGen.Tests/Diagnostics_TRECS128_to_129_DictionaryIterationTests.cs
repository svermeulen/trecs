using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

[TestFixture]
public class Diagnostics_TRECS128_to_129_DictionaryIterationTests
{
    const string GlobalCheckPrefix =
        "using System.Collections.Generic;\n"
        + "[assembly: Trecs.TrecsSourceGenSettings(GlobalCollectionIterationCheck = true)]\n";

    // ── Default behavior: only fires in fixed-update ISystem ───────────

    [Test]
    public void TRECS128_InFixedSystem_Fires()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dict = new Dictionary<int, string>();
                        foreach (var kv in dict) { }
                    }
                }
            }
            """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_InPresentationSystem_DoesNotFire()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Presentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dict = new Dictionary<int, string>();
                        foreach (var kv in dict) { }
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS128");
    }

    [Test]
    public void TRECS128_InNonSystem_DoesNotFire_ByDefault()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        var dict = new Dictionary<int, string>();
                        foreach (var kv in dict) { }
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS128");
    }

    // ── GlobalCollectionIterationCheck = true ──────────────────────────

    [Test]
    public void TRECS128_ForeachOverDictionary_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var dict = new System.Collections.Generic.Dictionary<int, string>();
                            foreach (var kv in dict) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverDictionaryKeys_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var dict = new Dictionary<int, string>();
                            foreach (var k in dict.Keys) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverDictionaryValues_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var dict = new Dictionary<int, string>();
                            foreach (var v in dict.Values) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverIDictionary_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            IDictionary<int, string> dict = new Dictionary<int, string>();
                            foreach (var kv in dict) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverIReadOnlyDictionary_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            IReadOnlyDictionary<int, string> dict = new Dictionary<int, string>();
                            foreach (var kv in dict) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_GetEnumeratorOnDictionary_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var dict = new Dictionary<int, string>();
                            var e = dict.GetEnumerator();
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_DictionaryIndexAccess_DoesNotFire()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var dict = new Dictionary<int, string>();
                            dict[1] = "hello";
                            var val = dict[1];
                            dict.TryGetValue(1, out var x);
                            dict.ContainsKey(1);
                        }
                    }
                }
                """;

        AssertDoesNotFire(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverList_DoesNotFire()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var list = new List<int>();
                            foreach (var x in list) { }
                        }
                    }
                }
                """;

        AssertDoesNotFire(source, "TRECS128");
    }

    // ── HashSet (global mode) ──────────────────────────────────────────

    [Test]
    public void TRECS128_ForeachOverHashSet_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var set = new HashSet<int>();
                            foreach (var x in set) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_HashSetContains_DoesNotFire()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var set = new HashSet<int>();
                            set.Add(1);
                            var has = set.Contains(1);
                        }
                    }
                }
                """;

        AssertDoesNotFire(source, "TRECS128");
    }

    // ── NativeHashMap (global mode) ────────────────────────────────────

    [Test]
    public void TRECS129_ForeachOverNativeHashMap_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var map = new Unity.Collections.NativeHashMap<int, int>();
                            foreach (var kv in map) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS129");
    }

    [Test]
    public void TRECS129_NativeHashMapIndexAccess_DoesNotFire()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var map = new Unity.Collections.NativeHashMap<int, int>();
                            map[1] = 42;
                            var val = map[1];
                        }
                    }
                }
                """;

        AssertDoesNotFire(source, "TRECS129");
    }

    [Test]
    public void TRECS129_ForeachOverNativeHashSet_Global_Fires()
    {
        var source =
            GlobalCheckPrefix
            + """
                namespace Sample
                {
                    public class Foo
                    {
                        public void Run()
                        {
                            var set = new Unity.Collections.NativeHashSet<int>();
                            foreach (var x in set) { }
                        }
                    }
                }
                """;

        AssertFires(source, "TRECS129");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    static void AssertFires(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new DictionaryIterationAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source, string id)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new DictionaryIterationAnalyzer() },
            source
        );
        var hit = diagnostics.FirstOrDefault(d => d.Id == id);
        Assert.That(hit, Is.Null, $"Unexpected {id}: {hit}");
    }

    static string Format(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsEmpty)
            return "  (none)";
        return string.Join(
            "\n",
            diagnostics.Select(d =>
                $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
            )
        );
    }
}
