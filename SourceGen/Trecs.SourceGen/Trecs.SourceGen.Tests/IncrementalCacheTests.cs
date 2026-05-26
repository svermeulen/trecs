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

    // -----------------------------------------------------------------------
    // Variant-coverage cache tests
    //
    // The baseline tests above cover one representative shape per generator.
    // The tests below exercise the remaining FromWorldFieldKind / AutoJobParamRoleKind /
    // JobIterationKind variants so a future regression that silently captures an
    // ITypeSymbol in a per-variant projection path is caught by CI.
    // -----------------------------------------------------------------------

    #region JobGenerator variant-coverage tests

    [Test]
    public void JobGenerator_NativeFactory_CachesAcrossUnrelatedEdits()
    {
        // FromWorldFieldKind.NativeFactory — cross-entity aspect factory on a
        // hand-written job struct. Exercises the NativeFactory-specific path in
        // FromWorldFieldEmitModel.From (field type display, AspectAttributeData
        // projection) and the factory's per-component dep tracking / lookup
        // creation in the emitter.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CMeal : Trecs.IEntityComponent { public float V; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public partial struct MealView : Trecs.IAspect, Trecs.IRead<CMeal> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct MealTag : Trecs.ITag { }

                public partial struct EatJob : Unity.Jobs.IJobFor
                {
                    [Trecs.FromWorld(Tag = typeof(MealTag))]
                    public MealView.NativeFactory MealLookup;

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
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
            $"JobGenerator (NativeFactory variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void JobGenerator_NativeComponentLookupWrite_CachesAcrossUnrelatedEdits()
    {
        // FromWorldFieldKind.NativeComponentLookupWrite — cross-group hoisted
        // write lookup. Exercises the NeedsHoistedGroups path, the per-group
        // dep tracking loop, and the CreateNativeComponentLookupWriteForJob +
        // RegisterPendingDispose emission.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }
                public struct ScoreTag : Trecs.ITag { }

                public partial struct ScoreJob : Unity.Jobs.IJobFor
                {
                    [Trecs.FromWorld(Tag = typeof(ScoreTag))]
                    public Trecs.NativeComponentLookupWrite<CScore> ScoreLookup;

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
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
            $"JobGenerator (NativeComponentLookupWrite variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void JobGenerator_CustomNonIteration_CachesAcrossUnrelatedEdits()
    {
        // JobIterationKind.CustomNonIteration — an IJob with no [ForEachEntity]
        // Execute. Only [FromWorld] fields drive the schedule overload. Exercises
        // the CustomNonIteration path in the JobModel projection and the IJob
        // schedule emission (Schedule, not ScheduleParallel).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
                public struct MoverTag : Trecs.ITag { }

                public partial struct GatherJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld(Tag = typeof(MoverTag))]
                    public MoverView.NativeFactory MoverLookup;

                    public void Execute() { }
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
            $"JobGenerator (CustomNonIteration variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void JobGenerator_CustomParallelIteration_CachesAcrossUnrelatedEdits()
    {
        // JobIterationKind.CustomParallelIteration — an IJobFor with a
        // user-written Execute(int) and [FromWorld] fields. Exercises the
        // custom-parallel path where the user provides the Execute body and
        // the generator only emits schedule wiring + dep tracking for the
        // [FromWorld] fields.
        const string source = """
            namespace Sample
            {
                public partial struct CHealth : Trecs.IEntityComponent { public int Value; }
                public struct HealthTag : Trecs.ITag { }

                public partial struct HealJob : Unity.Jobs.IJobFor
                {
                    [Trecs.FromWorld(Tag = typeof(HealthTag))]
                    public Trecs.NativeComponentBufferWrite<CHealth> HealthBuffer;

                    public void Execute(int index) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"JobGenerator (CustomParallelIteration variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void JobGenerator_ComponentsMode_CachesAcrossUnrelatedEdits()
    {
        // JobIterationKind.Components — components-mode iteration job with in/ref
        // component params and a tagged criterion. Exercises the ComponentsParamModel
        // projection path (vs the existing aspect-mode test).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public struct MoverTag : Trecs.ITag { }

                public partial struct MoveJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    public void Execute(in CPos pos, ref CVel vel) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"JobGenerator (Components-mode variant) did not cache.\n{result.Format()}"
        );
    }

    #endregion

    #region AutoJobGenerator variant-coverage tests

    [Test]
    public void AutoJobGenerator_ComponentsMode_CachesAcrossUnrelatedEdits()
    {
        // AutoJobIterationKindModel.Components — [WrapAsJob] with per-component
        // params instead of an aspect. Exercises the components-mode projection
        // path on AutoJobModel: AutoJobParamRoleKind.Component slots, per-param
        // buffer-field emission, and the components-mode IterationBuffers property.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public struct MoverTag : Trecs.ITag { }

                public partial class MoveSystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    [Trecs.WrapAsJob]
                    static void Move(in CPos pos, ref CVel vel) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoJobGenerator (Components-mode variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void AutoJobGenerator_NativeWorldAccessorAndGlobalIndex_CachesAcrossUnrelatedEdits()
    {
        // AutoJobParamRoleKind.NativeWorldAccessor + GlobalIndex — exercises the
        // HasNativeWorldAccessor and NeedsGlobalIndexOffset paths on AutoJobModel,
        // plus the __GlobalIndexOffset field emission and world.ToNative() wiring.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
                public struct MoverTag : Trecs.ITag { }

                public partial class PhysicsSystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    [Trecs.WrapAsJob]
                    static void Tick(
                        in MoverView view,
                        in Trecs.NativeWorldAccessor world,
                        [Trecs.GlobalIndex] int globalIndex) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoJobGenerator (NativeWorldAccessor + GlobalIndex variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void AutoJobGenerator_NativeSetCommandBuffer_CachesAcrossUnrelatedEdits()
    {
        // AutoJobParamRoleKind.NativeSetCommandBuffer via [FromWorld] — exercises the
        // NativeSetCommandBuffer projection path: no schedule param (set type on the
        // generic arg), CreateNativeSetCommandBufferForJob + dep tracking.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }
                public struct MoverTag : Trecs.ITag { }
                public struct EatingSet : Trecs.IEntitySet { }

                public partial class MoveSystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    [Trecs.WrapAsJob]
                    static void Move(
                        in MoverView view,
                        [Trecs.FromWorld] in Trecs.NativeSetCommandBuffer<EatingSet> eatingCmds) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoJobGenerator (NativeSetCommandBuffer variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void AutoJobGenerator_SingleEntityComponent_CachesAcrossUnrelatedEdits()
    {
        // AutoJobParamRoleKind.SingleEntityComponentRead/Write — exercises the
        // SingleEntity per-param projection on a [WrapAsJob] method with component
        // params (not aspect). Tests that the SingleEntityEmitTargetModel projection
        // doesn't leak symbols.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CScore : Trecs.IEntityComponent { public int Value; }
                public struct MoverTag : Trecs.ITag { }

                public partial class ScoreSystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    [Trecs.WrapAsJob]
                    static void Tally(
                        in CPos pos,
                        [Trecs.SingleEntity(Tag = typeof(Trecs.TrecsTags.Globals))] ref CScore score) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"AutoJobGenerator (SingleEntityComponent variant) did not cache.\n{result.Format()}"
        );
    }

    #endregion

    #region ForEach variant-coverage tests

    [Test]
    public void ForEachGenerator_WhitespaceOnlyRelevantEdit_CachesOrUnchanged()
    {
        // Verifies that whitespace shifts in the same source file don't bust the
        // cache. The generators' transform stages project to value-equatable models
        // that don't carry Location/TextSpan data, so a whitespace-only edit to
        // the source before the attributed method should leave SourceOutput as
        // Cached or Unchanged even though the syntax tree was replaced.
        const string sourceV1 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }

                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(in CPos pos, ref CVel vel) { }
                }
            }
            """;

        // Identical to V1 except extra blank lines before the class — shifts line
        // positions of the attributed method without changing its semantic content.
        const string sourceV2 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }



                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(in CPos pos, ref CVel vel) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.RunWithRelevantEdit(
            new IIncrementalGenerator[] { new ForEachGenerator(), new EntityComponentGenerator() },
            sourceV1,
            sourceV2
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"ForEachGenerator's source output was not cached across a whitespace-only edit. "
                + $"This means Location/TextSpan data is leaking into the pipeline model.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachGenerator_MatchByComponents_CachesAcrossUnrelatedEdits()
    {
        // MatchByComponents = true on [ForEachEntity] — exercises the MatchByComponents
        // flag on IterationCriteriaModel and the WithComponents call chain emission.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }

                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(MatchByComponents = true)]
                    void Move(in CPos pos, ref CVel vel) { }
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
            $"ForEachGenerator (MatchByComponents variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachGenerator_EntityHandle_CachesAcrossUnrelatedEdits()
    {
        // EntityHandle parameter on a components-mode ForEach method. Exercises the
        // EntityHandle-carrying path (NeedsEntityHandleBuffer, EntityRef emission).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class FindSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Find(in CPos pos, Trecs.EntityHandle handle) { }
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
            $"ForEachGenerator (EntityHandle variant) did not cache.\n{result.Format()}"
        );
    }

    #endregion

    #region ForEachEntityAspectGenerator variant-coverage tests

    [Test]
    public void ForEachEntityAspectGenerator_WhitespaceOnlyRelevantEdit_CachesOrUnchanged()
    {
        // Same whitespace-shift test for ForEachEntityAspectGenerator — verifies
        // the aspect-mode ForEach pipeline doesn't carry Location data either.
        const string sourceV1 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }

                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(in MoverView view) { }
                }
            }
            """;

        const string sourceV2 = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }



                public partial class MoveSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Move(in MoverView view) { }
                }
            }
            """;

        var result = IncrementalCacheTestHarness.RunWithRelevantEdit(
            new IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            sourceV1,
            sourceV2
        );

        Assert.That(
            result.IsCached(SourceOutputStepName),
            Is.True,
            $"ForEachEntityAspectGenerator's source output was not cached across a whitespace-only edit. "
                + $"This means Location/TextSpan data is leaking into the pipeline model.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachEntityAspectGenerator_MultipleAspectComponents_CachesAcrossUnrelatedEdits()
    {
        // Exercises an aspect with multiple read+write components (3 components
        // total) to cover the AspectBufferEntry list projection and ensure
        // the larger list doesn't introduce any non-equatable entries.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct CHealth : Trecs.IEntityComponent { public int Value; }
                public partial struct BigView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel, CHealth> { }

                public partial class BigSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(Trecs.TrecsTags.Globals))]
                    void Process(in BigView view) { }
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
            $"ForEachEntityAspectGenerator (multi-component aspect variant) did not cache.\n{result.Format()}"
        );
    }

    [Test]
    public void ForEachEntityAspectGenerator_EntityHandle_CachesAcrossUnrelatedEdits()
    {
        // Aspect-mode ForEach with an EntityHandle parameter. Exercises the
        // EntityHandle / EntityRef emission path alongside the aspect.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class FindSystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Find(in PlayerView view, Trecs.EntityHandle handle) { }
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
            $"ForEachEntityAspectGenerator (EntityHandle variant) did not cache.\n{result.Format()}"
        );
    }

    #endregion
}
