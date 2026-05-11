using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for TemplateDefinitionGenerator. The generator emits
/// a static <c>Template Template</c> field on every ITemplate type, calling the runtime
/// Template constructor with explicit named arguments. Regressions usually surface as a
/// missing namespace, a wrong constructor argument shape (param renaming), or a malformed
/// list initializer.
/// </summary>
[TestFixture]
public class TemplateDefinitionGeneratorTests
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

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

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
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void TemplateWithVariableUpdateOnlyAttribute_PassesFlagToCtor()
    {
        // Template-level [VariableUpdateOnly] is detected on the symbol's
        // attribute list and forwarded to the emitted Template constructor
        // as localVariableUpdateOnly: true. Regressions show up as either
        // a CS error (constructor arity mismatch with the runtime stub) or
        // a missing/false flag in the emitted source.
        const string source = """
            namespace Sample
            {
                [Trecs.VariableUpdateOnly]
                public partial class RenderOnlyTemplate : Trecs.ITemplate { }
            }
            """;

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var tree = string.Join("\n", run.GeneratedTrees);
        Assert.That(
            tree,
            Does.Contain("localVariableUpdateOnly: true"),
            "Expected emitted Template ctor to carry localVariableUpdateOnly: true.\n"
                + run.Format()
        );
    }

    [Test]
    public void TemplateWithoutVariableUpdateOnlyAttribute_DefaultsToFalse()
    {
        // Sanity check: a template without the attribute must emit
        // localVariableUpdateOnly: false so the runtime defaults to the
        // regular (sim-state) rule set.
        const string source = """
            namespace Sample
            {
                public partial class SimTemplate : Trecs.ITemplate { }
            }
            """;

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var tree = string.Join("\n", run.GeneratedTrees);
        Assert.That(
            tree,
            Does.Contain("localVariableUpdateOnly: false"),
            "Expected emitted Template ctor to carry localVariableUpdateOnly: false.\n"
                + run.Format()
        );
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

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void AbstractTemplate_MirrorsModifierAndPassesIsAbstractTrue()
    {
        // C# requires every partial declaration to agree on `abstract`; the generator
        // must mirror the modifier. Also verifies the runtime Template ctor gets
        // isAbstract: true so WorldBuilder.AddTemplate can guard at runtime.
        const string source = """
            namespace Sample
            {
                public abstract partial class AbsBase : Trecs.ITemplate { }
            }
            """;

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var tree = string.Join("\n", run.GeneratedTrees);
        Assert.That(
            tree,
            Does.Contain("abstract partial class AbsBase"),
            "Expected emitted partial to mirror the `abstract` modifier.\n" + run.Format()
        );
        Assert.That(
            tree,
            Does.Contain("isAbstract: true"),
            "Expected emitted Template ctor to carry isAbstract: true.\n" + run.Format()
        );
    }

    [Test]
    public void AbstractTemplate_WithExplicitDefault_CompilesCleanly()
    {
        // Abstract templates can carry explicit field defaults — the codegen emits a
        // private nested concrete subclass to drive field initializers, since `new T()`
        // doesn't compile against an abstract `T`.
        const string source = """
            namespace Sample
            {
                public partial struct CCount : Trecs.IEntityComponent { public int Value; }

                public abstract partial class AbsWithDefault : Trecs.ITemplate
                {
                    CCount Count = new() { Value = 7 };
                }

                public partial class ConcreteChild : Trecs.ITemplate, Trecs.IExtends<AbsWithDefault> { }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var tree = string.Join("\n", run.GeneratedTrees);
        Assert.That(
            tree,
            Does.Contain("_DefaultsHolder"),
            "Expected abstract template with explicit default to emit a private concrete subclass.\n"
                + run.Format()
        );
    }

    [Test]
    public void ConcreteTemplate_PassesIsAbstractFalse()
    {
        // Sanity check: a non-abstract template still emits isAbstract: false so
        // WorldBuilder.AddTemplate accepts it.
        const string source = """
            namespace Sample
            {
                public partial class Concrete : Trecs.ITemplate { }
            }
            """;

        var run = GeneratorTestHarness.Run(new TemplateDefinitionGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var tree = string.Join("\n", run.GeneratedTrees);
        Assert.That(
            tree,
            Does.Contain("isAbstract: false"),
            "Expected emitted Template ctor to carry isAbstract: false.\n" + run.Format()
        );
        Assert.That(
            tree,
            Does.Not.Contain("abstract partial class Concrete"),
            "Non-abstract template should not emit `abstract` modifier.\n" + run.Format()
        );
    }
}
