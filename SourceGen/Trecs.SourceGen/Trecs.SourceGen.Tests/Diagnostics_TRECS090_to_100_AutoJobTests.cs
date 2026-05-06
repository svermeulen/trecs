using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the [WrapAsJob] / AutoJob diagnostics group (TRECS090-100).
/// All emitted from AutoJobGenerator's validators.
///
/// Codes covered: 090, 091, 093, 094, 096, 097, 098, 100.
///
/// Codes intentionally not covered here:
/// - TRECS099 (NativeSetNotAllowedOnMainThread): emitted from
///   ParameterClassifier when a NativeSetRead/Write parameter appears in a
///   main-thread iteration method. Triggering it cleanly needs `NativeSetRead`
///   / `NativeSetWrite` stubs in TrecsStubs.cs (deferred).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS090_to_100_AutoJobTests
{
    [Test]
    public void TRECS090_WrapAsJobNonStatic()
    {
        // [WrapAsJob] methods must be static — non-static would have access to
        // class instance state, which can't survive job marshalling.
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
                    void NotStatic(in PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS090");
    }

    [Test]
    public void TRECS091_WrapAsJobWorldAccessorParam()
    {
        // [WrapAsJob] methods can't take WorldAccessor — jobs run off-thread and
        // need NativeWorldAccessor for structural ops.
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
                    static void BadJob(in PlayerView player, in Trecs.WorldAccessor world) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS091");
    }

    [Test]
    public void TRECS093_WrapAsJobManagedPassThrough()
    {
        // [PassThroughArgument] parameters become job fields. A managed type
        // (string) can't be a Burst-job field.
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
                    static void DoStuff(in PlayerView player, [Trecs.PassThroughArgument] string label) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS093");
    }

    [Test]
    public void TRECS094_WrapAsJobRefPassThrough()
    {
        // [PassThroughArgument] parameters are stored as value copies on the job
        // — ref/out wouldn't survive the marshal.
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
                    static void DoStuff(in PlayerView player, [Trecs.PassThroughArgument] ref int counter) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS094");
    }

    [Test]
    public void TRECS096_WrapAsJobEmptyCriteria()
    {
        // [WrapAsJob] needs Tags or MatchByComponents on its [ForEachEntity] —
        // the generated Schedule call needs criteria to walk groups.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity]
                    [Trecs.WrapAsJob]
                    static void DoStuff(in PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS096");
    }

    [Test]
    public void TRECS097_WrapAsJobExecutePassThrough()
    {
        // A [WrapAsJob] method named Execute serves as the ISystem entry point;
        // [PassThroughArgument] would change the Execute() signature.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Execute(in PlayerView player, [Trecs.PassThroughArgument] int dt) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS097");
    }

    [Test]
    public void TRECS098_SetAccessorInWrapAsJob()
    {
        // SetAccessor / SetRead / SetWrite are main-thread only. Inside a
        // [WrapAsJob] method, you need the job-side NativeSetRead / NativeSetWrite.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct PlayerSet : Trecs.IEntitySet { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void DoStuff(in PlayerView player, in Trecs.SetRead<PlayerSet> set) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS098");
    }

    [Test]
    public void TRECS100_UnrecognizedParameterType()
    {
        // A [SingleEntity] (or [ForEachEntity]) parameter whose type isn't a
        // recognized iteration / hoist / world / set shape needs explicit
        // [PassThroughArgument]. Triggered cleanly from RunOnceGenerator.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct UnknownType { public int V; }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    void DoStuff(
                        [Trecs.SingleEntity(Tag = typeof(PlayerTag))] in PlayerView player,
                        UnknownType junk
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS100", new IIncrementalGenerator[]
        {
            new RunOnceGenerator(),
            new IncrementalAspectGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        AssertDiagnostic(source, expectedId, new IIncrementalGenerator[]
        {
            new AutoJobGenerator(),
            new AutoSystemGenerator(),
            new IncrementalAspectGenerator(),
            new IncrementalEntityComponentGenerator(),
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
