using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for JobGenerator. The generator wraps user-written job structs
/// (partial structs implementing IJob/IJobFor) that have either an <c>Execute</c> method
/// decorated with <c>[ForEachEntity]</c> or one or more <c>[FromWorld]</c> fields. It emits
/// per-aspect lookup wiring and ScheduleParallel overloads that thread WorldAccessor's
/// JobScheduler dependency tracking through to the underlying Burst job.
/// </summary>
[TestFixture]
public class JobGeneratorTests
{
    [Test]
    public void IterationJob_WithReadAspect_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct UpdateJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_WithReadAndWriteAspect_CompilesCleanly()
    {
        // Read+write aspect surfaces both GetBufferReadForJob and GetBufferWriteForJob in
        // the emitted ScheduleParallel, plus IncludeWriteDep / TrackJobWrite paths in the
        // dependency tracking.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }

                public struct MoverTag : Trecs.ITag { }

                public partial struct UpdateJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    public void Execute(in MoverView mover) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_WithFromWorldField_CompilesCleanly()
    {
        // [FromWorld] field surfaces the cross-entity NativeFactory wiring path.
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

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_FromWorldPositionalCtorTag_CompilesCleanly()
    {
        // [FromWorld(typeof(Tag))] — positional-ctor shorthand on the FromWorld field.
        // Exercises the JobGenerator's hand-rolled FromWorld parser (now delegating
        // to InlineTagsParser.Parse) on the ConstructorArguments branch.
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
                    [Trecs.FromWorld(typeof(MealTag))]
                    public MealView.NativeFactory MealLookup;

                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    public void Execute(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_WithGlobalIndex_CompilesCleanly()
    {
        // [GlobalIndex] int on a component-iteration Execute method exercises the
        // NeedsGlobalIndexOffset wiring: the emitted job gets a private
        // _trecs_GlobalIndexOffset field, the call-site forwards
        // `_trecs_GlobalIndexOffset + i`, and the per-overload schedule path
        // assigns `_trecs_job._trecs_GlobalIndexOffset = _trecs_queryIndexOffset`.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct GatherJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in CPos pos, [Trecs.GlobalIndex] int globalIndex) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_ComponentsModeWithEntityHandle_CompilesCleanly()
    {
        // Components-mode method takes an EntityHandle. JobGenerator should plumb
        // a hidden `_trecs_EntityHandles` NativeEntityHandleBuffer field through
        // the Execute shim and the per-group ScheduleParallel body.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct UpdateJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in CPos pos, Trecs.EntityHandle handle) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_AspectModeWithEntityHandle_CompilesCleanly()
    {
        // Aspect-mode method takes (in AspectType, EntityHandle). The aspect
        // ExtraParamOrder list should record the EntityHandle and the Execute
        // call args should append `_trecs_EntityHandles[i]` after the aspect.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct UpdateJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(in PlayerView player, Trecs.EntityHandle handle) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_AspectModeWithEntityIndexAndEntityHandle_CompilesCleanly()
    {
        // Both EntityIndex and EntityHandle on the same aspect-mode method —
        // they're independent; ExtraParamOrder preserves declaration order so
        // the call args land in the right slots.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial struct UpdateJob : Unity.Jobs.IJobFor
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    public void Execute(
                        in PlayerView player,
                        Trecs.EntityIndex ei,
                        Trecs.EntityHandle handle
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void IterationJob_FromWorldGenericAttribute_CompilesCleanly()
    {
        // [FromWorld<Tag>] — C# 11 generic-attribute shorthand on the FromWorld field.
        // Exercises the JobGenerator's hand-rolled FromWorld parser on the
        // TypeArguments branch.
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
                    [Trecs.FromWorld<MealTag>]
                    public MealView.NativeFactory MealLookup;

                    [Trecs.ForEachEntity<PlayerTag>]
                    public void Execute(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new JobGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }
}
