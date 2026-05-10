using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for AutoJobGenerator. The generator transforms
/// <c>[WrapAsJob]</c> + <c>[ForEachEntity]</c> static methods into auto-generated
/// IJobFor structs marked <c>[BurstCompile]</c>, plus a ScheduleParallel(WorldAccessor, ...)
/// shim on the containing class. Tests cover the canonical user shapes (read-only aspect,
/// read+write, with [FromWorld] for cross-entity lookup, and with NativeWorldAccessor).
/// </summary>
[TestFixture]
public class AutoJobGeneratorTests
{
    [Test]
    public void AutoJob_StaticReadAspect_CompilesCleanly()
    {
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
                    static void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_WithNativeWorldAccessor_CompilesCleanly()
    {
        // [WrapAsJob] methods commonly take `in NativeWorldAccessor world` for
        // Burst-compatible world access (e.g. world.DeltaTime).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }

                public struct MoverTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(MoverTag))]
                    [Trecs.WrapAsJob]
                    static void Update(in MoverView mover, in Trecs.NativeWorldAccessor world) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_ComponentsModeWithEntityHandle_CompilesCleanly()
    {
        // Components-mode [WrapAsJob] takes an EntityHandle. AutoJobGenerator should
        // emit a hidden `_trecs_EntityHandles` NativeEntityHandleBuffer field on the
        // generated job struct, populated per-group at schedule time.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(in CPos pos, Trecs.EntityHandle handle) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_AspectModeWithEntityHandle_CompilesCleanly()
    {
        // Aspect-mode [WrapAsJob] taking (in AspectType, EntityHandle).
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
                    static void Process(in PlayerView player, Trecs.EntityHandle handle) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_WithFromWorldFactory_CompilesCleanly()
    {
        // [FromWorld] aspect-factory parameter — the canonical FeedingFrenzy pattern. The
        // emitted job struct gets a NativeFactory field plus the wiring to allocate /
        // dispose its underlying lookups.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CMeal : Trecs.IEntityComponent { public float V; }

                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public partial struct MealView : Trecs.IAspect, Trecs.IRead<CMeal> { }

                public struct PlayerTag : Trecs.ITag { }
                public struct MealTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Eat(
                        in PlayerView player,
                        in Trecs.NativeWorldAccessor world,
                        [Trecs.FromWorld(Tag = typeof(MealTag))] in MealView.NativeFactory mealLookup
                    ) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_PositionalCtorTag_CompilesCleanly()
    {
        // [ForEachEntity(typeof(Tag))] + [WrapAsJob] — positional-ctor shorthand on the
        // AutoJob path. Exercises the attribute parser's ConstructorArguments branch.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_GenericAttribute_CompilesCleanly()
    {
        // [ForEachEntity<Tag>] + [WrapAsJob] — generic-attribute shorthand on the
        // AutoJob path. Exercises the attribute parser's TypeArguments branch.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity<PlayerTag>]
                    [Trecs.WrapAsJob]
                    static void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AutoJob_WithGlobalIndex_CompilesCleanly()
    {
        // [GlobalIndex] int on a [WrapAsJob] static method exercises the
        // NeedsGlobalIndexOffset wiring on the AutoJobGenerator path: the emitted
        // job struct gets an internal _trecs_GlobalIndexOffset field, the call-site
        // forwards `_trecs_GlobalIndexOffset + i`, and the generated
        // ScheduleParallel(QueryBuilder, ...) overload accumulates a
        // _trecs_queryIndexOffset across the per-group loop. Mirrors
        // JobGeneratorTests.IterationJob_WithGlobalIndex_CompilesCleanly so the
        // user-facing API reads identically across both job-generation paths.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    [Trecs.WrapAsJob]
                    static void Process(in CPos pos, [Trecs.GlobalIndex] int globalIndex) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new AutoJobGenerator(),
                new AutoSystemGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }
}
