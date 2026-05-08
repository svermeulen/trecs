using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Job scheduling diagnostics group (TRECS070-079). Most are
/// emitted from JobGenerator's validators when a job struct's [FromWorld] /
/// [ForEachEntity] surface violates a scheduling-shape rule. TRECS070 is the
/// exception — it's emitted by <see cref="RawScheduleMethodAnalyzer"/>, a
/// <see cref="Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer"/>, and is
/// driven through <see cref="GeneratorTestHarness.RunAnalyzers"/>.
///
/// Codes covered: 070, 071, 073, 074, 075, 076, 077, 078, 079.
///
/// Codes intentionally not covered here:
/// - TRECS072: gap in numbering; no descriptor.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS070_to_079_JobSchedulingTests
{
    [Test]
    public void TRECS070_RawScheduleParallelOnJobWithTrecsField()
    {
        // A job struct with a Trecs typed field (NativeComponentBufferRead<T>)
        // calling Unity's IJobForExtensions.ScheduleParallel directly should fire
        // TRECS070. The user must call the JobGenerator-emitted ScheduleParallel
        // member overload (which threads the world's dep tracking) instead.
        const string source = """
            namespace Sample
            {
                using Unity.Jobs;

                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct MyJob : Unity.Jobs.IJobFor
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Positions;

                    public void Execute(int index) { }
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var job = new MyJob();
                        job.ScheduleParallel(10, 1, default);
                    }
                }
            }
            """;

        AssertAnalyzerDiagnostic(
            source,
            "TRECS070",
            new DiagnosticAnalyzer[] { new RawScheduleMethodAnalyzer() }
        );
    }

    [Test]
    public void TRECS071_UnsupportedFromWorldFieldType()
    {
        // [FromWorld] expects one of the documented Native* container types — a
        // bare int isn't one of them.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct BadJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public int NotAValidContainer;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS071");
    }

    [Test]
    public void TRECS073_JobNestedInsideGenericOuterType()
    {
        // JobGenerator can't redeclare an outer type's type parameters on its
        // emitted Schedule overloads. Nested-in-generic is rejected.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class Outer<T>
                {
                    public partial struct InnerJob : Unity.Jobs.IJobFor
                    {
                        [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                        public void Execute(in PlayerView player) { }
                    }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS073");
    }

    [Test]
    public void TRECS074_JobNotPartial()
    {
        // A job struct with [FromWorld]/[ForEachEntity] markers needs to be
        // partial — the generator emits Schedule overloads onto it.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public struct NonPartialJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS074");
    }

    [Test]
    public void TRECS075_MultiVariableFromWorldField()
    {
        // [FromWorld] must be on a single-variable field declaration so the
        // generator can wire each container unambiguously.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct MultiJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> A, B;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS075");
    }

    [Test]
    public void TRECS076_CustomJobMissingExecuteMethod()
    {
        // A custom job (has [FromWorld] fields, no [ForEachEntity]) must define
        // Execute() — without it the generator can't wrap it as IJob.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct NoExecJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Buffer;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS076");
    }

    [Test]
    public void TRECS077_CustomJobExecuteHasParameter()
    {
        // Without [ForEachEntity], a custom job's Execute must be parameterless
        // (or single-int for IJobFor) — anything else is a wiring mismatch.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct BadExecJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Buffer;

                    public void Execute(float dt) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS077");
    }

    [Test]
    public void TRECS078_CustomJobExecuteNotPublic()
    {
        // A custom job's Execute must be public so it directly satisfies the
        // IJob/IJobFor interface contract.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct PrivateExecJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Buffer;

                    void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS078");
    }

    [Test]
    public void TRECS079_ParallelJobWriteFieldMissingNativeDisableParallel()
    {
        // Parallel iteration jobs that write via [FromWorld] need
        // [NativeDisableParallelForRestriction] — Unity's job walker rejects the
        // job otherwise.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct ParallelWriteJob : Unity.Jobs.IJobFor
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferWrite<CVel> Velocities;

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS079");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }

    static void AssertAnalyzerDiagnostic(
        string source,
        string expectedId,
        DiagnosticAnalyzer[] analyzers
    )
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(analyzers, source);
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{FormatDiagnostics(diagnostics)}"
        );
    }

    static string FormatDiagnostics(
        System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics
    )
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
