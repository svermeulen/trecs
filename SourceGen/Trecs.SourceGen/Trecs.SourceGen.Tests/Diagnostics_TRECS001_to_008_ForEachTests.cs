using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the ForEach diagnostics group (TRECS001-008). Each test feeds
/// minimal source that should trigger the named diagnostic and asserts the generator
/// emits it. These guard against a refactor that silently drops or reworks one of the
/// validation rules.
///
/// TRECS006 was deleted as a dead descriptor (no emit site).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS001_to_008_ForEachTests
{
    [Test]
    public void TRECS001_AspectParameterMissingInModifier()
    {
        // The aspect parameter on an [ForEachEntity] aspect-iteration method must be
        // declared `in`. A bare `PlayerView player` trips ForEachEntityAspectGenerator's
        // modifier check.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS001",
            new IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            }
        );
    }

    [Test]
    public void TRECS002_NoParameters()
    {
        // [ForEachEntity] requires at least one per-entity parameter.
        const string source = """
            namespace Sample
            {
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process() { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS002",
            new IIncrementalGenerator[] { new ForEachGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS003_NonVoidReturn()
    {
        // [ForEachEntity] must return void.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    int Process(in CPos pos) { return 0; }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS003",
            new IIncrementalGenerator[] { new ForEachGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS004_NonPartialContainingClass()
    {
        // [ForEachEntity] requires the containing class to be partial (the generator
        // emits a partial overload onto it).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in CPos pos) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS004",
            new IIncrementalGenerator[] { new ForEachGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS005_JobIterationFirstParamNotIAspect()
    {
        // [ForEachEntity] Execute on a job validates that its first parameter implements
        // IAspect (the aspect-iteration job shape). Any other type trips JobGenerator's
        // invalid-parameter-list rule.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }
                public struct NotAnAspect { }

                public partial struct BadJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in NotAnAspect a) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS005",
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS007_TwoExecuteMethodsOnIterationJob()
    {
        // A job struct can declare at most one [ForEachEntity] Execute method.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct DoubleJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView a) { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView a, Trecs.EntityIndex ei) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS007",
            new IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            }
        );
    }

    [Test]
    public void TRECS008_TwoEntityIndexParametersOnComponentJob()
    {
        // Component-iteration jobs validate parameter shape; two EntityIndex parameters
        // is an explicit "only one is allowed" error path.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct BadJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in CPos a, Trecs.EntityIndex one, Trecs.EntityIndex two) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS008",
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS008_TwoGlobalIndexParametersOnComponentJob()
    {
        // Component-iteration jobs validate parameter shape; two [GlobalIndex] parameters
        // is an explicit "only one is allowed" error path. This guards the "more than one
        // [GlobalIndex] parameter" branch in JobGenerator.ValidateForEachComponentsMethod.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct BadJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in CPos a, [Trecs.GlobalIndex] int one, [Trecs.GlobalIndex] int two) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS008",
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() }
        );
    }

    [Test]
    public void TRECS008_TwoGlobalIndexParametersOnWrapAsJobMethod()
    {
        // The [WrapAsJob] (AutoJobGenerator) path runs its own parameter classifier,
        // so the "only one [GlobalIndex] is allowed" rule needs its own coverage there.
        // This guards the duplicate-detection branch in AutoJobGenerator.Validate.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(in CPos a, [Trecs.GlobalIndex] int one, [Trecs.GlobalIndex] int two) { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS008",
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new EntityComponentGenerator(),
            }
        );
    }

    static void AssertDiagnostic(
        string source,
        string expectedId,
        IIncrementalGenerator[] generators
    )
    {
        var run = GeneratorTestHarness.Run(generators, source);
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }
}
