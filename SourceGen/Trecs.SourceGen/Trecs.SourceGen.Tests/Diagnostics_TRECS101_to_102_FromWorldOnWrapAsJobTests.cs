using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the [FromWorld] on [WrapAsJob] diagnostics group
/// (TRECS101-102). Both emitted from AutoJobGenerator's per-parameter
/// validator when a [FromWorld]-marked parameter on a [WrapAsJob] method
/// violates a wiring rule.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS101_to_102_FromWorldOnWrapAsJobTests
{
    [Test]
    public void TRECS101_FromWorldUnsupportedTypeOnWrapAsJob()
    {
        // [FromWorld] on an unrecognized type (not one of the supported Native*
        // containers) is rejected on the WrapAsJob path.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct UnknownContainer { public int V; }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(
                        in PlayerView player,
                        [Trecs.FromWorld] in UnknownContainer junk
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS101");
    }

    [Test]
    public void TRECS102_FromWorldRequiresInlineTagsOnWrapAsJob()
    {
        // Group-resolved [FromWorld] containers on [WrapAsJob] need inline
        // Tag/Tags — the generated wrapper has no runtime TagSet plumbing.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CMeal : Trecs.IEntityComponent { public float V; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(
                        in PlayerView player,
                        [Trecs.FromWorld] in Trecs.NativeComponentBufferRead<CMeal> meals
                    ) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS102");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{run.Format()}"
        );
    }
}
