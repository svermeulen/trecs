using System.Linq;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for RunOnceGenerator. Routed to from any method whose
/// parameters are decorated with one or more <c>[FromSingleEntity]</c> attributes (and which
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
                        [Trecs.FromSingleEntity(Tag = typeof(GlobalsTag))] in GlobalsView globals
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
    public void RunOnce_TwoFromSingleEntityParams_CompilesCleanly()
    {
        // Multiple [FromSingleEntity] params each get their own hoisted lookup. Catches a
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
                        [Trecs.FromSingleEntity(Tag = typeof(PlayerTag))] in PlayerView player,
                        [Trecs.FromSingleEntity(Tag = typeof(GlobalsTag))] in GlobalsView globals
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
                            [Trecs.FromSingleEntity(Tag = typeof(GlobalsTag))] in CConfig config
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
        // [FromSingleEntity] on a component param (rather than aspect) routes through the
        // ComponentBuffer lookup path instead of the aspect ctor path.
        const string source = """
            namespace Sample
            {
                public partial struct CConfig : Trecs.IEntityComponent { public int V; }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Apply(
                        [Trecs.FromSingleEntity(Tag = typeof(GlobalsTag))] in CConfig config
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
        // Positional-ctor shorthand: [FromSingleEntity(typeof(Tag))] — must resolve the
        // tag the same way as [FromSingleEntity(Tag = typeof(Tag))].
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct GlobalsTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Initialize(
                        [Trecs.FromSingleEntity(typeof(GlobalsTag))] in GlobalsView globals
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
        // params Type[] expansion: [FromSingleEntity(typeof(A), typeof(B))].
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
                        [Trecs.FromSingleEntity(typeof(PlayerTag), typeof(AliveTag))] in PlayerView p
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
        // C# 11 generic-attribute shorthand: [FromSingleEntity<GlobalsTag>] —
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
                        [Trecs.FromSingleEntity<GlobalsTag>] in GlobalsView globals
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
        // [FromSingleEntity<A, B>] — multi-arity generic variant.
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
                        [Trecs.FromSingleEntity<PlayerTag, AliveTag>] in PlayerView p
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
                        [Trecs.FromSingleEntity(typeof(GlobalsTag), Tag = typeof(OtherTag))] in GlobalsView g
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

    [Test]
    public void RunOnce_FromGlobalEntity_CompilesCleanly()
    {
        // [FromGlobalEntity] is shorthand for [FromSingleEntity(typeof(TrecsTags.Globals))].
        // The source gen resolves TrecsTags.Globals from the attribute's containing assembly.
        const string source = """
            namespace Sample
            {
                public partial struct CScore : Trecs.IEntityComponent { public int V; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CScore> { }

                public partial class MySystem
                {
                    void Execute(
                        [Trecs.FromGlobalEntity] in GlobalsView globals
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
    public void RunOnce_FromGlobalEntityComponent_CompilesCleanly()
    {
        // [FromGlobalEntity] on a component-typed parameter (not just aspects).
        const string source = """
            namespace Sample
            {
                public partial struct CScore : Trecs.IEntityComponent { public int V; }

                public partial class MySystem
                {
                    void Execute(
                        [Trecs.FromGlobalEntity] in CScore score
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
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void RunOnce_FromGlobalEntityMixedWithFromSingleEntity_CompilesCleanly()
    {
        // Mix [FromGlobalEntity] and [FromSingleEntity] on different params.
        const string source = """
            namespace Sample
            {
                public partial struct CScore : Trecs.IEntityComponent { public int V; }
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct GlobalsView : Trecs.IAspect, Trecs.IRead<CScore> { }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    void Execute(
                        [Trecs.FromGlobalEntity] in GlobalsView globals,
                        [Trecs.FromSingleEntity(Tag = typeof(PlayerTag))] in PlayerView player
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
    public void RunOnce_FromGlobalEntity_GlobalsTagUnresolvable_ReportsTRECS119()
    {
        // TRECS119 fires when [FromGlobalEntity] is used but the attribute's containing
        // assembly does not expose Trecs.TrecsTags.Globals (real-world cause: the assembly
        // uses [FromGlobalEntity] without referencing com.trecs.core). InlineTagsParser
        // resolves Globals from FromGlobalEntityAttribute.ContainingAssembly, so to reach
        // the failure path the compilation must define [FromGlobalEntity] but omit
        // TrecsTags.Globals — hence a self-contained source compiled WITHOUT the default
        // stubs (which always define TrecsTags.Globals).
        const string source = """
            namespace Trecs
            {
                public interface ITag { }
                public interface IEntityComponent { }

                [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field)]
                public sealed class FromGlobalEntityAttribute : System.Attribute { }

                // NOTE: deliberately NO TrecsTags.Globals here — that is the whole point.
            }

            namespace Sample
            {
                public struct CScore : Trecs.IEntityComponent { public int V; }

                public partial class MySystem
                {
                    void Execute([Trecs.FromGlobalEntity] in CScore score) { }
                }
            }
            """;

        var run = GeneratorTestHarness.RunWithoutDefaultStubs(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[] { new RunOnceGenerator() },
            source
        );

        Assert.That(
            run.GenDiagnostics.Any(d => d.Id == "TRECS119"),
            Is.True,
            "Expected TRECS119 ([FromGlobalEntity] could not resolve TrecsTags.Globals) "
                + "when the attribute's assembly does not expose TrecsTags.Globals. Got: "
                + run.Format()
        );
    }
}
