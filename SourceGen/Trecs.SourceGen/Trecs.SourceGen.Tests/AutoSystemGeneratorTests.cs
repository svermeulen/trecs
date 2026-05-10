using System.Linq;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for AutoSystemGenerator. The generator emits a partial that
/// adds <c>: Trecs.Internal.ISystemInternal</c> to the user's ISystem class and provides
/// the <c>WorldAccessor _world</c> field, <c>World</c> property, explicit interface impls
/// for <c>ISystemInternal.World</c> / <c>ISystemInternal.Ready</c> / <c>ISystemInternal.Shutdown</c>,
/// plus <c>partial void OnReady()</c> and <c>partial void OnShutdown()</c> hooks. Regressions
/// usually surface as the emitted partial failing to merge with the user's class (wrong
/// type-parameter list, missing namespace, duplicated explicit-interface members).
/// </summary>
[TestFixture]
public class AutoSystemGeneratorTests
{
    [Test]
    public void MinimalSystem_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new AutoSystemGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(
            run.GeneratedTrees,
            Is.Not.Empty,
            "Expected the generator to emit at least one file."
        );
    }

    [Test]
    public void GenericSystem_PreservesTypeParameterListAndConstraints()
    {
        // Regression guard: AutoSystemGenerator captures the user's type-parameter list and
        // constraint clauses verbatim. Dropping either would cause CS0260 (mismatched
        // partials) or a constraint mismatch error when the partials merge.
        const string source = """
            namespace Sample
            {
                public partial class GenericSystem<T> : Trecs.ISystem where T : struct
                {
                    public void Execute() { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new AutoSystemGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void NestedSystem_CompilesCleanly()
    {
        // ISystem classes nested inside a partial outer type need the generator to emit
        // matching outer-type scopes (same constraint as components).
        const string source = """
            namespace Sample
            {
                public partial class Outer
                {
                    public partial class InnerSystem : Trecs.ISystem
                    {
                        public void Execute() { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new AutoSystemGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void EmitsOnShutdownPartialAndInterfaceImpl()
    {
        // The generator must emit both the user-facing `partial void OnShutdown()` and
        // the explicit `ISystemInternal.Shutdown()` implementation that calls it. Without
        // both, World.Dispose's OnShutdown wiring would either fail to find the user's
        // partial or fail to satisfy ISystemInternal.
        const string source = """
            namespace Sample
            {
                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new AutoSystemGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());

        var generated = string.Join("\n", run.GeneratedTrees.Select(t => t.ToString()));
        Assert.That(
            generated,
            Does.Contain("partial void OnShutdown();"),
            "Expected `partial void OnShutdown();` in generated output.\n" + run.Format()
        );
        Assert.That(
            generated,
            Does.Contain("Trecs.Internal.ISystemInternal.Shutdown()"),
            "Expected explicit `ISystemInternal.Shutdown()` impl in generated output.\n"
                + run.Format()
        );
    }

    [Test]
    public void UserDefinedOnShutdown_CompilesCleanly()
    {
        // A user-implemented `partial void OnShutdown()` must merge with the generator's
        // declaration without producing CS0759 / CS0761 errors.
        const string source = """
            namespace Sample
            {
                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    partial void OnShutdown()
                    {
                        // user teardown
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new AutoSystemGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
    }
}
