using System.Linq;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness + diagnostic tests for RemovalFieldHandlerGenerator (the
/// [CascadeRemove] / [DisposeOnRemove] feature). Compile tests assert the emitted
/// IComponentRemovalHandlers partial recompiles against the runtime stubs;
/// diagnostic tests assert TRECS134/135 fire on invalid field types.
/// </summary>
[TestFixture]
public class RemovalFieldHandlerGeneratorTests
{
    [Test]
    public void CascadeRemove_HandleList_And_SingleHandle_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct Owner : Trecs.IEntityComponent
                {
                    [Trecs.CascadeRemove]
                    public Trecs.TrecsList<Trecs.EntityHandle> Children;
                }

                public partial struct Child : Trecs.IEntityComponent
                {
                    [Trecs.CascadeRemove]
                    public Trecs.EntityHandle Parent;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty, run.Format());
    }

    [Test]
    public void DisposeOnRemove_AllSupportedTypes_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public class MyClass { }
                public struct MyVal { }

                public partial struct Bag : Trecs.IEntityComponent
                {
                    [Trecs.DisposeOnRemove] public Trecs.TrecsList<MyVal> List;
                    [Trecs.DisposeOnRemove] public Trecs.UniquePtr<MyClass> Unique;
                    [Trecs.DisposeOnRemove] public Trecs.SharedPtr<MyClass> Shared;
                    [Trecs.DisposeOnRemove] public Trecs.NativeUniquePtr<MyVal> NativeUnique;
                    [Trecs.DisposeOnRemove] public Trecs.NativeSharedPtr<MyVal> NativeShared;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty, run.Format());
    }

    [Test]
    public void Composition_BothAttributesOnSameList_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct Owner : Trecs.IEntityComponent
                {
                    [Trecs.CascadeRemove, Trecs.DisposeOnRemove]
                    public Trecs.TrecsList<Trecs.EntityHandle> Children;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty, run.Format());
    }

    [Test]
    public void NoAnnotatedFields_EmitsNothing()
    {
        const string source = """
            namespace Sample
            {
                public partial struct Plain : Trecs.IEntityComponent
                {
                    public int Value;
                    public Trecs.TrecsList<Trecs.EntityHandle> Children;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(run.GeneratedTrees, Is.Empty, run.Format());
    }

    [Test]
    public void TRECS134_CascadeRemove_OnInvalidType_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct Bad : Trecs.IEntityComponent
                {
                    [Trecs.CascadeRemove]
                    public int NotAHandle;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(
            run.GenDiagnostics.Any(d => d.Id == "TRECS134"),
            Is.True,
            run.Format()
        );
    }

    [Test]
    public void TRECS135_DisposeOnRemove_OnInvalidType_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct Bad : Trecs.IEntityComponent
                {
                    [Trecs.DisposeOnRemove]
                    public Trecs.EntityHandle Handle;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new RemovalFieldHandlerGenerator(), source);

        Assert.That(
            run.GenDiagnostics.Any(d => d.Id == "TRECS135"),
            Is.True,
            run.Format()
        );
    }
}
