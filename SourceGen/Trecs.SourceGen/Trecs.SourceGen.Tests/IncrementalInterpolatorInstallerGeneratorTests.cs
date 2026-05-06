using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for IncrementalInterpolatorInstallerGenerator. The generator
/// scans for static methods marked with <c>[GenerateInterpolatorSystem(systemName, groupName)]</c>
/// and groups them per groupName into a generated <c>Add{Group}(this WorldBuilder)</c>
/// extension method that calls <c>AddInterpolatedPreviousSaver&lt;T&gt;()</c> +
/// <c>AddSystem(new {systemName}())</c> for each one.
///
/// Tests use a separate `MyInterpolationSystem`-style class so the generator's emitted
/// <c>new MyInterpolationSystem()</c> reference resolves; in real usage that class is
/// emitted by IncrementalInterpolatorJobGenerator (covered separately).
/// </summary>
[TestFixture]
public class IncrementalInterpolatorInstallerGeneratorTests
{
    [Test]
    public void SingleInterpolator_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public class PosInterpolationSystem : Trecs.ISystem { public void Execute() { } }

                public static class Registrations
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                    static void RegisterPos(CPos c) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalInterpolatorInstallerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty);
    }

    [Test]
    public void MultipleInterpolatorsSameGroup_EmitSingleExtensionMethod()
    {
        // Two interpolators sharing a group should fold into one Add{Group}() that
        // chains both AddInterpolatedPreviousSaver/AddSystem pairs. Catches a regression
        // in the per-group folding (e.g. emitting two extension methods with the same name
        // → CS0111 duplicate member).
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CRot : Trecs.IEntityComponent { public float W; }

                public class PosInterpolationSystem : Trecs.ISystem { public void Execute() { } }
                public class RotInterpolationSystem : Trecs.ISystem { public void Execute() { } }

                public static class Registrations
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                    static void RegisterPos(CPos c) { }

                    [Trecs.GenerateInterpolatorSystem("RotInterpolationSystem", "Movement")]
                    static void RegisterRot(CRot c) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalInterpolatorInstallerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void NestedRegistrationClass_CompilesCleanly()
    {
        // The generated WorldBuilder extension class lands at namespace scope, independent
        // of where the [GenerateInterpolatorSystem] method lives. Confirms the generator
        // doesn't trip over a nested registration class shape.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public class PosInterpolationSystem : Trecs.ISystem { public void Execute() { } }

                public partial class Outer
                {
                    public static partial class InnerRegistrations
                    {
                        [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                        static void RegisterPos(CPos c) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalInterpolatorInstallerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void InterpolatorsInDifferentGroups_EmitSeparateExtensionMethods()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public partial struct CHealth : Trecs.IEntityComponent { public float V; }

                public class PosSystem : Trecs.ISystem { public void Execute() { } }
                public class HealthSystem : Trecs.ISystem { public void Execute() { } }

                public static class Registrations
                {
                    [Trecs.GenerateInterpolatorSystem("PosSystem", "Movement")]
                    static void RegisterPos(CPos c) { }

                    [Trecs.GenerateInterpolatorSystem("HealthSystem", "Combat")]
                    static void RegisterHealth(CHealth c) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new IncrementalInterpolatorInstallerGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
