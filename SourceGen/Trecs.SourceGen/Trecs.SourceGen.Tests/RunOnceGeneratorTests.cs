using System.Linq;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for RunOnceGenerator. Routed to from any method whose
/// parameters are decorated with one or more <c>[SingleEntity]</c> attributes (and which
/// has no <c>[ForEachEntity]</c> attribute). The generator hoists each singleton lookup
/// via <c>__world.Query().WithTags&lt;...&gt;().SingleIndex()</c> and calls the user
/// method exactly once with the resolved aspects/components plugged in.
/// </summary>
[TestFixture]
public class RunOnceGeneratorTests
{
    [Test]
    public void RunOnce_SingleAspectParam_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity(Tag = typeof(GlobalsTag))] in GlobalsView globals
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_TwoSingleEntityParams_CompilesCleanly()
    {
        // Multiple [SingleEntity] params each get their own hoisted lookup. Catches a
        // regression in per-param lookup variable naming or ordering.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CHealth : Trecs.IEntityComponent { public float V; }

                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CHealth> { }

                public struct PlayerTag : Trecs.ITag { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Run(
                        [Trecs.SingleEntity(Tag = typeof(PlayerTag))] in PlayerView player,
                        [Trecs.SingleEntity(Tag = typeof(GlobalsTag))] in GlobalsView globals
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_NestedSystemClass_CompilesCleanly()
    {
        // Same nested-scope concern as ForEach / ForEachEntityAspect — the generator must walk
        // the system class's containing-type chain so the emitted partial merges with the
        // user's nested class.
        const string source = """
            namespace Sample
            {
                public partial struct CConfig : Trecs.IEntityComponent { public int V; }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class Outer
                {
                    public partial class InnerSystem
                    {
                        void Apply(
                            [Trecs.SingleEntity(Tag = typeof(GlobalsTag))] in CConfig config
                        ) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_ComponentParam_CompilesCleanly()
    {
        // [SingleEntity] on a component param (rather than aspect) routes through the
        // ComponentBuffer lookup path instead of the aspect ctor path.
        const string source = """
            namespace Sample
            {
                public partial struct CConfig : Trecs.IEntityComponent { public int V; }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Apply(
                        [Trecs.SingleEntity(Tag = typeof(GlobalsTag))] in CConfig config
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_PositionalCtorTag_CompilesCleanly()
    {
        // Positional-ctor shorthand: [SingleEntity(typeof(Tag))] — must resolve the
        // tag the same way as [SingleEntity(Tag = typeof(Tag))].
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity(typeof(GlobalsTag))] in GlobalsView globals
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_PositionalCtorMultipleTags_CompilesCleanly()
    {
        // params Type[] expansion: [SingleEntity(typeof(A), typeof(B))].
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct AliveTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity(typeof(PlayerTag), typeof(AliveTag))] in PlayerView p
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_GenericAttribute_CompilesCleanly()
    {
        // C# 11 generic-attribute shorthand: [SingleEntity<GlobalsTag>] —
        // tags pulled from AttributeClass.TypeArguments.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity<GlobalsTag>] in GlobalsView globals
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_GenericAttributeMultipleTags_CompilesCleanly()
    {
        // [SingleEntity<A, B>] — multi-arity generic variant.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct AliveTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity<PlayerTag, AliveTag>] in PlayerView p
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_PositionalCtorAndNamedTag_ReportsConflict()
    {
        // Mixing the positional ctor with the named Tag/Tags property is ambiguous —
        // expect TRECS053 (TagAndTagsBothSpecified).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct GlobalsTag : Trecs.ITag { }
                public struct OtherTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.SingleEntity(typeof(GlobalsTag), Tag = typeof(OtherTag))] in GlobalsView g
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RunOnceGenerator(), source);

        Assert.That(
            run.GenDiagnostics.Any(d => d.Id == "TRECS053"),
            Is.True,
            "Expected TRECS053 (TagAndTagsBothSpecified) when mixing positional ctor "
                + "with named Tag/Tags. Got: "
                + run.Format()
        );
    }
}
