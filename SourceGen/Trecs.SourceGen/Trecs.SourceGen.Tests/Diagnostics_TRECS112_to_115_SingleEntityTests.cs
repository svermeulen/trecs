using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the per-parameter / per-field [SingleEntity] diagnostics
/// group (TRECS112-116). Most emit from JobGenerator / AutoJobGenerator /
/// RunOnceGenerator's per-parameter validators when a [SingleEntity]-marked
/// target violates a wiring rule. TRECS116 fires when a parallel job's
/// [SingleEntity] aspect field carries IWrite components without
/// [NativeDisableParallelForRestriction].
/// </summary>
[TestFixture]
public class Diagnostics_TRECS112_to_115_SingleEntityTests
{
    [Test]
    public void TRECS112_SingleEntityOnNonAspectOrComponentType()
    {
        // [SingleEntity] resolves to a matched entity, so the parameter must
        // either be an aspect (IAspect) or a component (IEntityComponent).
        const string source = """
            namespace Sample
            {
                public struct PlayerTag : Trecs.ITag { }
                public struct NotAComponent { public int V; }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    void DoStuff(
                        [Trecs.SingleEntity(typeof(PlayerTag))] in NotAComponent target
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS112", new IIncrementalGenerator[]
        {
            new RunOnceGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS113_SingleEntityWrongModifier()
    {
        // [SingleEntity] aspect param must be `in`. A bare (no modifier)
        // aspect parameter trips the modifier check.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(
                        in PlayerView player,
                        [Trecs.SingleEntity(typeof(PlayerTag))] PlayerView singleton
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS113", new IIncrementalGenerator[]
        {
            new AutoJobGenerator(),
            new AutoSystemGenerator(),
            new AspectGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS114_SingleEntityMissingInlineTags()
    {
        // [SingleEntity] needs inline Tag/Tags — there's no runtime override for
        // the singleton resolution.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(
                        in PlayerView player,
                        [Trecs.SingleEntity] in PlayerView singleton
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS114", new IIncrementalGenerator[]
        {
            new AutoJobGenerator(),
            new AutoSystemGenerator(),
            new AspectGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS115_SingleEntityWithFromWorld()
    {
        // [SingleEntity] alone carries the world-sourced semantics; combining
        // it with [FromWorld] is double-marking and rejected.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    void DoStuff(
                        [Trecs.SingleEntity(typeof(PlayerTag))][Trecs.FromWorld] in PlayerView player
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS115", new IIncrementalGenerator[]
        {
            new RunOnceGenerator(),
            new AspectGenerator(),
            new EntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS116_ParallelJobSingleEntityWriteAspectMissingNativeDisableParallel()
    {
        // A parallel iteration job with a hand-written [SingleEntity] aspect
        // field whose aspect contains IWrite components ships a
        // NativeComponentBufferWrite via the materialized aspect — Unity's
        // parallel-job safety walker rejects it without
        // [NativeDisableParallelForRestriction] on the field.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IWrite<CPos> { }
                public partial struct EnemyView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct EnemyTag : Trecs.ITag { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial struct ParallelJob : Unity.Jobs.IJobFor
                {
                    [Trecs.SingleEntity(typeof(GlobalsTag))]
                    public GlobalsView Globals;

                    [Trecs.ForEachEntity(Tag = typeof(EnemyTag))]
                    public void Execute(in EnemyView enemy) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS116", new IIncrementalGenerator[]
        {
            new JobGenerator(),
            new AspectGenerator(),
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
