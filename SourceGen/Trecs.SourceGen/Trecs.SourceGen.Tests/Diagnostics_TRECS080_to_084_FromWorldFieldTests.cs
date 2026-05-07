using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the [FromWorld] field validation diagnostics group
/// (TRECS080-084). All emitted from JobGenerator's per-field validator.
///
/// Codes covered: 081, 083, 084.
///
/// Codes intentionally not covered here:
/// - TRECS082 (FromWorldInlineTagsNotSupportedForEntityIndex): triggers require
///   the `NativeComponentRead&lt;T&gt;` / `NativeComponentWrite&lt;T&gt;` (single,
///   not buffer) stubs in TrecsStubs.cs. The buffer flavors are stubbed; the
///   single flavors aren't. Deferred until a follow-up that extends the stubs.
/// - TRECS080: gap in numbering; no descriptor.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS080_to_084_FromWorldFieldTests
{
    [Test]
    public void TRECS081_TrecsContainerFieldMissingFromWorld()
    {
        // A Trecs container type on a job (with other [FromWorld] fields, so the
        // job is recognized as a Trecs job) must itself have [FromWorld] for
        // dependency tracking.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct MissingFromWorldJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Marked;

                    // Same container type, no [FromWorld] — should fire TRECS081.
                    public Trecs.NativeComponentBufferRead<CPos> Forgotten;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS081");
    }

    [Test]
    public void TRECS083_FromWorldTooManyInlineTags()
    {
        // [FromWorld(...)] supports at most 4 tag types (TagSet's arity limit).
        // Passing 5 trips the inline-tags parser.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public struct TagA : Trecs.ITag { }
                public struct TagB : Trecs.ITag { }
                public struct TagC : Trecs.ITag { }
                public struct TagD : Trecs.ITag { }
                public struct TagE : Trecs.ITag { }

                public partial struct ManyTagsJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld(typeof(TagA), typeof(TagB), typeof(TagC), typeof(TagD), typeof(TagE))]
                    public Trecs.NativeComponentBufferRead<CPos> Buffer;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS083");
    }

    [Test]
    public void TRECS084_FromWorldInlineTagsOnNativeWorldAccessor()
    {
        // NativeWorldAccessor is constructed via world.ToNative() — there's no
        // group resolution to apply tags to.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct NwaJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld(typeof(PlayerTag))]
                    public Trecs.NativeWorldAccessor World;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS084");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }
}
