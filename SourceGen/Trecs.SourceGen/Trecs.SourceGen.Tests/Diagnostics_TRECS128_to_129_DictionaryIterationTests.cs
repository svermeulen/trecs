using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

[TestFixture]
public class Diagnostics_TRECS128_to_129_DictionaryIterationTests
{
    // ── TRECS128: Dictionary<K,V> iteration ────────────────────────────

    [Test]
    public void TRECS128_ForeachOverDictionary_Fires()
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

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverDictionaryKeys_Fires()
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
                        foreach (var k in dict.Keys) { }
                    }
                }
            }
            """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_ForeachOverDictionaryValues_Fires()
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
                        foreach (var v in dict.Values) { }
                    }
                }
            }
            """;

        AssertFires(source, "TRECS128");
    }

    // ── TRECS128: IDictionary / IReadOnlyDictionary iteration ─────────

    [Test]
    public void TRECS128_ForeachOverIDictionary_Fires()
    {
        const string source = """
            using System.Collections.Generic;
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
    public void TRECS128_ForeachOverIReadOnlyDictionary_Fires()
    {
        const string source = """
            using System.Collections.Generic;
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
    public void TRECS128_IDictionaryIndexAccess_DoesNotFire()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        IDictionary<int, string> dict = new Dictionary<int, string>();
                        dict[1] = "hello";
                        var val = dict[1];
                        dict.ContainsKey(1);
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS128");
    }

    [Test]
    public void TRECS128_GetEnumeratorOnDictionary_Fires()
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
        const string source = """
            using System.Collections.Generic;
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
        const string source = """
            using System.Collections.Generic;
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

    // ── TRECS128: HashSet<T> iteration ─────────────────────────────────

    [Test]
    public void TRECS128_ForeachOverHashSet_Fires()
    {
        const string source = """
            using System.Collections.Generic;
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
    public void TRECS128_GetEnumeratorOnHashSet_Fires()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        var set = new HashSet<int>();
                        var e = set.GetEnumerator();
                    }
                }
            }
            """;

        AssertFires(source, "TRECS128");
    }

    [Test]
    public void TRECS128_HashSetContains_DoesNotFire()
    {
        const string source = """
            using System.Collections.Generic;
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

    // ── TRECS129: NativeHashMap iteration ──────────────────────────────

    [Test]
    public void TRECS129_ForeachOverNativeHashMap_Fires()
    {
        const string source = """
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
    public void TRECS129_GetEnumeratorOnNativeHashMap_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        var map = new Unity.Collections.NativeHashMap<int, int>();
                        var e = map.GetEnumerator();
                    }
                }
            }
            """;

        AssertFires(source, "TRECS129");
    }

    [Test]
    public void TRECS129_ForeachOverNativeParallelHashMap_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        var map = new Unity.Collections.NativeParallelHashMap<int, int>();
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
        const string source = """
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

    // ── TRECS129: NativeHashSet iteration ─────────────────────────────

    [Test]
    public void TRECS129_ForeachOverNativeHashSet_Fires()
    {
        const string source = """
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

    [Test]
    public void TRECS129_NativeHashSetContains_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class Foo
                {
                    public void Run()
                    {
                        var set = new Unity.Collections.NativeHashSet<int>();
                        var has = set.Contains(1);
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS129");
    }

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
