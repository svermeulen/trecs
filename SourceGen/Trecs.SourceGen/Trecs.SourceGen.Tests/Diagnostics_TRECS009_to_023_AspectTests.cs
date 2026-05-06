using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Aspect diagnostics group (TRECS009-023). Most of these are
/// raised by IncrementalAspectGenerator's validator (or its component-type helper)
/// when the user declares an aspect struct or an [Unwrap] component that violates
/// the contract.
///
/// Codes covered: 009, 012, 013, 016, 022, 023.
///
/// Codes intentionally not covered here:
/// - TRECS010 / TRECS011 / TRECS014: deleted as dead descriptors.
/// - TRECS015 (CouldNotResolveSymbol): generic catch-all used across many generators
///   for null-symbol error-recovery paths. Hard to trigger reliably from valid input.
/// - TRECS020 (AspectInterfaceMustBePartial): already covered by
///   AspectInterfaceDiagnosticTests.NonPartial_AspectInterface_EmitsMustBePartial.
/// - TRECS017-019, 021: gaps in numbering; no descriptor.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS009_to_023_AspectTests
{
    [Test]
    public void TRECS009_AspectStructMustBePartial()
    {
        // An [Aspect] struct has to be partial — the generator emits constructors,
        // ref-returning properties, and a NativeFactory onto it.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                [Trecs.Aspect(typeof(CPos))]
                public struct PlayerView : Trecs.IAspect { }
            }
            """;

        AssertAspectDiagnostic(source, "TRECS009");
    }

    [Test]
    public void TRECS012_UnwrapComponentMustImplementIEntityComponent()
    {
        // An [Unwrap] type must implement IEntityComponent so the generator knows it's
        // a real ECS component rather than a random struct.
        const string source = """
            namespace Sample
            {
                [Trecs.Unwrap]
                public partial struct CFloat { public float V; }
            }
            """;

        AssertAspectDiagnostic(source, "TRECS012");
    }

    [Test]
    public void TRECS013_UnwrapComponentMustHaveExactlyOneField()
    {
        // [Unwrap]'s purpose is to expose a single backing field as the component's
        // value — a struct with two fields can't be unwrapped unambiguously.
        const string source = """
            namespace Sample
            {
                [Trecs.Unwrap]
                public partial struct CTwoFields : Trecs.IEntityComponent
                {
                    public float A;
                    public float B;
                }
            }
            """;

        AssertAspectDiagnostic(source, "TRECS013");
    }

    [Test]
    public void TRECS016_DuplicateComponentTypeInAspect()
    {
        // Listing the same component as both IRead and IWrite is rejected — the
        // generator would emit duplicate property names.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct PlayerView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CPos> { }
            }
            """;

        AssertAspectDiagnostic(source, "TRECS016");
    }

    [Test]
    public void TRECS022_AspectParamMustUseInModifier()
    {
        // [ForEachEntity] aspect parameter must be `in`, not `ref` — aspects expose
        // ref readonly / ref properties internally, so the loop hands them out by
        // readonly reference.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(ref PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS022", new IIncrementalGenerator[]
        {
            new ForEachAspectGenerator(),
            new IncrementalAspectGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS023_AspectWithNoComponents()
    {
        // An aspect with no IRead/IWrite components has nothing to iterate — the
        // generator rejects the iteration call.
        const string source = """
            namespace Sample
            {
                public partial struct EmptyView : Trecs.IAspect { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in EmptyView view) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS023", new IIncrementalGenerator[]
        {
            new ForEachAspectGenerator(),
            new IncrementalAspectGenerator(),
        });
    }

    static void AssertAspectDiagnostic(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunGenerator(source);
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{FormatDiagnostics(diagnostics)}"
        );
    }

    static void AssertDiagnostic(string source, string expectedId, IIncrementalGenerator[] generators)
    {
        var run = GeneratorTestHarness.Run(generators, source);
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{run.Format()}"
        );
    }

    static string FormatDiagnostics(System.Collections.Generic.IEnumerable<Diagnostic> diagnostics)
    {
        var list = diagnostics.ToList();
        if (list.Count == 0)
            return "No diagnostics were emitted.";
        return "Actual diagnostics:\n"
            + string.Join("\n", list.Select(d => $"  {d.Id}: {d.GetMessage()}"));
    }
}
