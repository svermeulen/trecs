using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for ForEachEntityAspectGenerator. Routed to from any
/// <c>[ForEachEntity]</c> method whose first parameter implements <c>IAspect</c>. The
/// generator emits convenience overloads that drive the aspect's own per-aspect
/// AspectQuery / Enumerator machinery (which AspectGenerator emits on the
/// aspect type itself).
/// </summary>
[TestFixture]
public class ForEachEntityAspectGeneratorTests
{
    [Test]
    public void ForEachEntity_AspectMode_WithSingleTag_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_WithMultipleTags_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public partial struct MoverView
                    : Trecs.IAspect, Trecs.IRead<CPos>, Trecs.IWrite<CVel> { }

                public struct TagA : Trecs.ITag { }
                public struct TagB : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tags = new[] { typeof(TagA), typeof(TagB) })]
                    void Update(in MoverView mover) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_NestedSystemClass_CompilesCleanly()
    {
        // [ForEachEntity] methods on a system class nested inside an outer class need the
        // generator to emit matching outer-type scopes so the emitted partial merges with
        // the user's nested class. Without this, the emitted partial declares
        // `partial class InnerSystem` at namespace scope and resolves the user's Process()
        // method to a generated overload of the same name (CS1615 on `in __view`).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class Outer
                {
                    public partial class InnerSystem
                    {
                        [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                        void Process(in PlayerView player) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_WithMatchByComponents_CompilesCleanly()
    {
        // MatchByComponents iterates over any group whose archetype contains the aspect's
        // declared components (no tag filter). Catches a regression in the alternate
        // criteria-derivation path versus the explicit Tag/Tags one above.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(MatchByComponents = true)]
                    void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_PositionalCtorTag_CompilesCleanly()
    {
        // [ForEachEntity(typeof(Tag))] — positional-ctor shorthand on the iteration
        // attribute, exercised through IterationCriteriaParser's ConstructorArguments
        // branch.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_GenericAttribute_CompilesCleanly()
    {
        // [ForEachEntity<Tag>] — C# 11 generic-attribute shorthand, exercised through
        // IterationCriteriaParser's TypeArguments branch.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity<PlayerTag>]
                    void Process(in PlayerView player) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_WithEntityHandle_CompilesCleanly()
    {
        // Aspect-mode iteration with an extra EntityHandle parameter — generator
        // resolves the handle once per iteration alongside the aspect view.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    void Process(in PlayerView player, Trecs.EntityHandle handle) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachEntity_AspectMode_WithEntityAccessor_CompilesCleanly()
    {
        // Aspect-mode iteration with an extra EntityAccessor parameter — generator
        // emits __world.Entity(__entityIndex) once per iteration.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct PlayerView : Trecs.IAspect, Trecs.IRead<CPos> { }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    void Process(in PlayerView player, Trecs.EntityAccessor entity) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachEntityAspectGenerator(),
                new AspectGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }
}
