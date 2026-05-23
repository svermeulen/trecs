using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Validates that the migrated generators' pipelines actually hit Roslyn's incremental
/// cache when nothing observable about their input has changed. See
/// <see cref="IncrementalCacheTestHarness"/> for the protocol — these tests assert that
/// the <c>SourceOutput</c> step (the terminal stage that calls <c>AddSource</c>) is
/// reused from the previous run when an unrelated source tree is edited between runs.
///
/// <para>A failure here means a non-equatable value (Roslyn symbol, syntax node, raw
/// <see cref="Diagnostic"/>, plain <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// of reference-typed elements, etc.) is leaking through the pipeline. The reasons map
/// in the assertion message tells you which step lost the cache.</para>
/// </summary>
[TestFixture]
public class IncrementalCacheTests
{
    // SourceOutput is Roslyn's internal name for the terminal stage created by
    // RegisterSourceOutput. It's the load-bearing assertion target — if this stage is
    // Cached/Unchanged across two runs, the generator successfully skipped re-doing the
    // codegen work even though the compilation changed.
    private const string SourceOutputStepName = "SourceOutput";

    /// <summary>
    /// Negative test pinning the assertion's tightness: when the relevant source actually
    /// changes (the aspect's base list gains an IWrite), the SourceOutput step must
    /// re-run. <see cref="CacheRunResult.IsCached"/> already requires <c>reasons.Count
    /// &gt; 0</c>, so a missing step-name key fails positive tests rather than passing
    /// vacuously — but this test is the belt to that suspenders: it confirms the
    /// SourceOutput pipeline actually observes model changes we'd expect it to.
    /// </summary>
    [Test]
    public void AspectGenerator_DoesNotCacheAcrossRelevantEdits()
    {
        const string sourceV1 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct ReaderView : Trecs.IAspect, Trecs.IRead<CPos> { }
            }
            """;
        const string sourceV2 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct ReaderView : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
            }
            """;

        var result = IncrementalCacheTestHarness.RunWithRelevantEdit(
            new IIncrementalGenerator[] { new AspectGenerator(), new EntityComponentGenerator() },
            sourceV1,
            sourceV2
        );

        Assert.That(
            result.HasMisses(SourceOutputStepName),
            Is.True,
            $"Expected SourceOutput step to re-run when the aspect's component set changed. If both this and the positive tests pass with HasMisses=false, the step name lookup is broken.\n{result.Format()}"
        );
    }

    [Test]
    public void AspectGenerator_CachesAcrossUnrelatedEdits()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct ReaderView : Trecs.IAspect, Trecs.IRead<CPos> { }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[] { new AspectGenerator(), new EntityComponentGenerator() },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AspectGenerator's source output did not cache. Cache miss = pipeline is leaking a non-equatable value.\n{result.Format()}"
        );
    }

    [Test]
    public void TemplateDefinitionGenerator_CachesAcrossUnrelatedEdits()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CHealth : Trecs.IEntityComponent { public float Value; }
                public partial class HealthyTemplate
                    : Trecs.ITemplate, Trecs.ITagged<Trecs.TrecsTags.Globals>
                {
                    CHealth Health;
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"TemplateDefinitionGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void EntityComponentGenerator_CachesAcrossUnrelatedEdits()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; public float Y; }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[] { new EntityComponentGenerator() },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"EntityComponentGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void InterpolatorJobGenerator_CachesAcrossUnrelatedEdits()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public static class Lerper
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolator", "Movement")]
                    public static void Lerp(in CPos previous, in CPos current, ref CPos interpolated, float t)
                        => interpolated.X = previous.X + (current.X - previous.X) * t;
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new InterpolatorJobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"InterpolatorJobGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void AutoSystemGenerator_CachesAcrossUnrelatedEdits()
    {
        // Minimal ISystem subclass with one iteration method — exercises the iteration-
        // method collection and custom-param walk that previously stored ITypeSymbol
        // on CustomParamInfo. After migration, only the namespace string remains.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity]
                    void Move(ref CPos pos) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoSystemGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoSystemGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void RunOnceGenerator_CachesAcrossUnrelatedEdits()
    {
        // A partial system with one run-once method: a [SingleEntity] aspect param +
        // a [SingleEntity] component param. Exercises the hoisted-singleton model
        // (HoistedSingletonModel + HoistedAspectComponent) plus the equatable param-
        // slot list. If anything in those carries a non-equatable value (symbols,
        // syntax, raw Diagnostic), the SourceOutput step won't cache.
        const string source = """
            namespace Sample
            {
                public partial struct CHealth : Trecs.IEntityComponent { public float Value; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CHealth>, Trecs.IWrite<CScore> { }

                public partial class GameSystem : Trecs.ISystem
                {
                    void DoOnce(
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] in PlayerView player,
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] in CScore score)
                    { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new RunOnceGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"RunOnceGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachGenerator_CachesAcrossUnrelatedEdits()
    {
        // Components-mode iteration with: in/ref component params, a tagged criterion,
        // a hoisted [SingleEntity] component param, and a [PassThroughArgument] custom
        // param. Exercises the value-equatable ForEachComponentValidation including
        // its precomputed criteria chain and namespace set.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }

                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(
                        in CPos pos,
                        ref CVel vel,
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] in CScore score,
                        [Trecs.PassThroughArgument] float dt)
                    { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[] { new ForEachGenerator(), new EntityComponentGenerator() },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"ForEachGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachEntityAspectGenerator_CachesAcrossUnrelatedEdits()
    {
        // Aspect-mode iteration with: an in IAspect view, a hoisted [SingleEntity]
        // component param, a [PassThroughArgument] custom param, and a tagged
        // criterion. Exercises the value-equatable ForEachAspectValidation
        // including the precomputed AspectBufferEntry list (with VarName) and
        // criteria chain.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public partial struct MoverView : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }

                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(
                        in MoverView view,
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] in CScore score,
                        [Trecs.PassThroughArgument] float dt)
                    { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"ForEachEntityAspectGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void JobGenerator_CachesAcrossUnrelatedEdits()
    {
        // Aspect-iteration job struct with [FromWorld] container, a hoisted
        // [SingleEntity] aspect field, and tagged criterion. Exercises the
        // JobModel projection: AspectIterationModel.AspectComponents,
        // FromWorldFieldEmitModel projection, SingleEntityFieldModel projection,
        // and the precomputed AttributeCriteriaChain string.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct CHealth : Trecs.IEntityComponent { public int Value; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public partial struct MoverView : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
                public partial struct ScoreView : Trecs.IAspect, Trecs.IWrite<CScore> { }

                public partial struct MoverJob
                {
                    [Trecs.FromWorld] private Trecs.NativeComponentBufferRead<CHealth> healths;

                    [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    private ScoreView score;

                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    public void Execute(in MoverView view) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"JobGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void AutoJobGenerator_CachesAcrossUnrelatedEdits()
    {
        // Aspect-iteration [WrapAsJob] method with a [FromWorld] container, a
        // hoisted [SingleEntity] aspect parameter, a PassThrough parameter, and
        // a tagged criterion. Exercises the AutoJobModel projection:
        // AutoJobAspectModel.Components, FromWorldFieldEmitModel /
        // SingleEntityEmitTargetModel projections (cross-referenced via the
        // FromWorldIndex / SingleEntityIndex slots on AutoJobParamModel), and
        // the precomputed AttributeCriteriaChain string + AdditionalUsings.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct CHealth : Trecs.IEntityComponent { public int Value; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public partial struct MoverView : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
                public partial struct ScoreView : Trecs.IAspect, Trecs.IWrite<CScore> { }

                public partial class MoverSystem
                {
                    [Trecs.WrapAsJob]
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    static void Tick(
                        in MoverView view,
                        [Trecs.FromWorld] in Trecs.NativeComponentBufferRead<CHealth> healths,
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] in ScoreView score,
                        [Trecs.PassThroughArgument] float dt)
                    { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoJobGenerator's source output did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void InterpolatorInstallerGenerator_CachesAcrossUnrelatedEdits()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public static class Lerper
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolator", "Movement")]
                    public static void Lerp(in CPos previous, in CPos current, ref CPos interpolated, float t)
                        => interpolated.X = previous.X + (current.X - previous.X) * t;
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new InterpolatorInstallerGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"InterpolatorInstallerGenerator's source output did not cache.\n{result.Format()}"
        );
    }
}
