using System.Linq;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for IncrementalEntityComponentGenerator. The generator emits
/// Equals / GetHashCode / operator overloads for partial structs implementing IEntityComponent;
/// regressions usually surface as the emitted partial failing to compile (wrong type-parameter
/// list, missing namespace, double [Serializable], etc.).
/// </summary>
[TestFixture]
public class IncrementalEntityComponentGeneratorTests
{
    [Test]
    public void SimpleComponent_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPosition : Trecs.IEntityComponent
                {
                    public float X;
                    public float Y;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalEntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(
            run.GeneratedTrees,
            Is.Not.Empty,
            "Expected the generator to emit at least one file."
        );
    }

    [Test]
    public void GenericComponent_PreservesTypeParameterList()
    {
        // Regression guard: dropping <T> from the emitted partial would silently emit a
        // phantom non-generic type that nothing merges into. The generator captures
        // TypeParameterList for exactly this reason — assert the result still compiles.
        const string source = """
            namespace Sample
            {
                public partial struct CGeneric<T> : Trecs.IEntityComponent
                    where T : unmanaged
                {
                    public T Value;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalEntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ComponentWithExistingSerializable_DoesNotDoubleApplyAttribute()
    {
        // The generator skips its own [Serializable] when the user has already written one,
        // because SerializableAttribute has AllowMultiple=false and applying twice is CS0579.
        // This test would catch a regression that re-enabled emission unconditionally.
        const string source = """
            namespace Sample
            {
                [System.Serializable]
                public partial struct CSerialized : Trecs.IEntityComponent
                {
                    public int V;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalEntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        // CS0579 = "Duplicate attribute" — name it specifically so a regression points
        // straight at the right cause.
        Assert.That(
            run.CompileDiagnostics.Any(d => d.Id == "CS0579"),
            Is.False,
            "Generator emitted a duplicate [Serializable] attribute.\n" + run.Format()
        );
    }

    [Test]
    public void NestedComponent_EmitsContainingTypeScopes()
    {
        // Components nested inside a partial outer type need the generator to emit matching
        // outer-type scopes. A regression that dropped containing-type tracking would
        // produce code that doesn't compile because the inner partial has no enclosing scope.
        const string source = """
            namespace Sample
            {
                public partial class Outer
                {
                    public partial struct CInner : Trecs.IEntityComponent
                    {
                        public int V;
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalEntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
