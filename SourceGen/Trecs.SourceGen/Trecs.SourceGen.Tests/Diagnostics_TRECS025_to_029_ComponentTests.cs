using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Component / iteration diagnostics group (TRECS025-029).
/// These rules govern parameter shapes on [ForEachEntity] / [WrapAsJob] / [SingleEntity]
/// methods — the parameter classifier and the AutoJob assembler are the main emitters.
///
/// Codes covered: 025, 026, 027, 028, 029.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS025_to_029_ComponentTests
{
    [Test]
    public void TRECS025_DuplicateAspectParameterOnAutoJob()
    {
        // [WrapAsJob] static method may declare at most one aspect parameter — a
        // second one trips DuplicateLoopParameter (the "aspect" flavor).
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
                    static void Process(in PlayerView a, in PlayerView b) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS025", new IIncrementalGenerator[]
        {
            new AutoJobGenerator(),
            new AutoSystemGenerator(),
            new IncrementalAspectGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS026_MixedAspectAndComponentParams()
    {
        // An [ForEachEntity] method can't take both an aspect parameter and a direct
        // component parameter — the aspect already declares its component requirements
        // through IRead/IWrite, so a second component param would shadow them.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct BadJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player, in CVel vel) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS026", new IIncrementalGenerator[]
        {
            new JobGenerator(),
            new IncrementalAspectGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS027_SetReadParameterMissingInModifier()
    {
        // SetRead<T> parameters must be passed `in` — the classifier rejects ref/none.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerSet : Trecs.IEntitySet { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in CPos pos, Trecs.SetRead<PlayerSet> set) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS027", new IIncrementalGenerator[]
        {
            new IncrementalForEachGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS028_SetAccessorParameterUsesRefModifier()
    {
        // SetAccessor<T> must be passed by value (or `in`); `ref` is rejected because
        // the accessor is itself a thin handle that the loop hands out.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerSet : Trecs.IEntitySet { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in CPos pos, ref Trecs.SetAccessor<PlayerSet> set) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS028", new IIncrementalGenerator[]
        {
            new IncrementalForEachGenerator(),
            new IncrementalEntityComponentGenerator(),
        });
    }

    [Test]
    public void TRECS029_ComponentParamMissingInOrRef()
    {
        // Component-iteration methods pass each component as `in` (read) or `ref`
        // (write); no modifier means the loop can't hand the buffer slot out
        // safely.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(CPos pos) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS029", new IIncrementalGenerator[]
        {
            new IncrementalForEachGenerator(),
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
