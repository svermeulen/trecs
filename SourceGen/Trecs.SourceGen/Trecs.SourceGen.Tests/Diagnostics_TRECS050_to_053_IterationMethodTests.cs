using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Iteration method diagnostics group (TRECS050-053).
/// Codes covered: 050, 051, 053.
///
/// TRECS052 is a gap in numbering; no descriptor.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS050_to_053_IterationMethodTests
{
    [Test]
    public void TRECS050_IterationMethodIsStatic()
    {
        // [ForEachEntity] inside a job struct cannot be static — JobGenerator wires
        // the job's instance fields ([FromWorld], component buffers) onto the call,
        // and a static method has no instance to receive them.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct MyJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public static void Execute(in PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS050", new IIncrementalGenerator[]
        {
            new JobGenerator(),
            new AspectGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS051_IterationMethodIsAbstract()
    {
        // [ForEachEntity] on an abstract method has no body to dispatch to — the
        // generated wrapper would call into thin air.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public abstract partial class MySystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    protected abstract void Process(in CPos pos);

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS051", new IIncrementalGenerator[]
        {
            new AutoSystemGenerator(),
            new ForEachGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS053_BothTagAndTagsSpecified()
    {
        // [ForEachEntity(Tag = ..., Tags = ...)] is ambiguous — pick exactly one.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct TagA : Trecs.ITag { }
                public struct TagB : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(TagA), Tags = new[] { typeof(TagB) })]
                    void Process(in CPos pos) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS053", new IIncrementalGenerator[]
        {
            new ForEachGenerator(),
            new EntityComponentGenerator(),
        });
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
}
