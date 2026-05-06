using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for IncrementalTemplateDefinitionGenerator. The generator emits
/// a static <c>Template Template</c> field on every ITemplate type, calling the runtime
/// Template constructor with explicit named arguments. Regressions usually surface as a
/// missing namespace, a wrong constructor argument shape (param renaming), or a malformed
/// list initializer.
/// </summary>
[TestFixture]
public class IncrementalTemplateDefinitionGeneratorTests
{
    [Test]
    public void EmptyTemplate_CompilesCleanly()
    {
        // Smallest possible template: no base templates, no partitions, no components, no tags.
        // Generator should still emit Template = new Template(... Array.Empty<...>() ...).
        const string source = """
            namespace Sample
            {
                public partial class EmptyTemplate : Trecs.ITemplate { }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalTemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty);
    }

    [Test]
    public void TemplateWithComponents_CompilesCleanly()
    {
        // Components show up in the emitted localComponentDeclarations array as
        // ComponentDeclaration<T>(null, null, null, null, null, null, null, null) entries.
        // Catches a regression in the no-config branch (ComponentDeclaration constructor arity).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CVel : Trecs.IEntityComponent { public float X; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    public CPos Position;
                    public CVel Velocity;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new IncrementalTemplateDefinitionGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void NestedTemplate_CompilesCleanly()
    {
        // Nested under a partial outer class — verifies the containing-type emission path
        // matches what the entity-component / aspect generators do.
        const string source = """
            namespace Sample
            {
                public partial class Outer
                {
                    public partial class InnerTemplate : Trecs.ITemplate { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalTemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
