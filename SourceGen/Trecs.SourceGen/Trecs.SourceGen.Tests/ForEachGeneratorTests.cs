using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for ForEachGenerator. Routed to from any
/// <c>[ForEachEntity]</c> method whose parameters are all components (no aspect). The
/// generator emits convenience overloads that take WorldAccessor / QueryBuilder /
/// SparseQueryBuilder and iterate via <c>GroupSlices()</c>, calling the user method
/// once per matching entity with `in`/`ref` component buffers.
/// </summary>
[TestFixture]
public class ForEachGeneratorTests
{
    [Test]
    public void ForEachWithReadComponent_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct MyTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(MyTag))]
                    void Process(in CPos pos) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachWithReadAndWriteComponents_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }
                public struct MyTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(MyTag))]
                    void Update(in CPos pos, ref CVel vel) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachWithNestedSystemClass_CompilesCleanly()
    {
        // Same nested-scope concern as ForEachEntityAspect / RunOnce — the generator must walk
        // the system class's containing-type chain so the emitted partial merges with the
        // user's nested class.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class Outer
                {
                    public partial class InnerSystem
                    {
                        [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                        void Process(in CPos pos) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachWithGenericOuterClass_CompilesCleanly()
    {
        // Outer<T> must round-trip through the wrapper emit — without the
        // type-parameter list, the emitted `partial class Outer { }` would
        // be a different type from the user's `Outer<T>` and fail to merge.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class Outer<T>
                {
                    public partial class InnerSystem
                    {
                        [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                        void Process(in CPos pos) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachWithMultipleTags_CompilesCleanly()
    {
        // Multi-tag iteration uses the WithTags<T1, T2, ...> arity 2 overload — exercises
        // a different chain than the single-tag form above.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct TagA : Trecs.ITag { }
                public struct TagB : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(Tags = new[] { typeof(TagA), typeof(TagB) })]
                    void Process(in CPos pos) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachPositionalCtorTag_CompilesCleanly()
    {
        // [ForEachEntity(typeof(Tag))] — positional-ctor shorthand. Exercises the
        // attribute parser's ConstructorArguments branch on the component-iteration path.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity(typeof(PlayerTag))]
                    void Process(in CPos pos) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ForEachGenericAttribute_CompilesCleanly()
    {
        // [ForEachEntity<Tag>] — C# 11 generic-attribute shorthand. Exercises the
        // attribute parser's TypeArguments branch on the component-iteration path.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem
                {
                    [Trecs.ForEachEntity<PlayerTag>]
                    void Process(in CPos pos) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
