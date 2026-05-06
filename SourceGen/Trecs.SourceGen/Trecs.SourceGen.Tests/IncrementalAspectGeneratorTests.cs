using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for IncrementalAspectGenerator. The diagnostic-validation side
/// (aspect-interface partial requirement, cycle handling, etc.) lives in
/// <see cref="AspectInterfaceDiagnosticTests"/>. This fixture asserts that aspect structs
/// produce code that *compiles*: aspects emit a substantial machinery layer (component
/// fields, multiple constructors, ref-returning properties, a per-aspect Query DSL with
/// dense + sparse Enumerator support, and a NativeFactory for cross-entity Burst access),
/// so most regressions surface as the emitted partial failing to compile.
/// </summary>
[TestFixture]
public class IncrementalAspectGeneratorTests
{
    [Test]
    public void AspectStruct_WithSingleRead_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct ReaderView : Trecs.IAspect, Trecs.IRead<CPos> { }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new IncrementalAspectGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AspectStruct_WithReadAndWrite_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new IncrementalAspectGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AspectStruct_InNestedClass_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial class Outer
                {
                    public partial struct CFoo : Trecs.IEntityComponent { public int V; }
                    public partial struct InnerView : Trecs.IAspect, Trecs.IRead<CFoo> { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new IncrementalAspectGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
