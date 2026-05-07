using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for InterpolatorJobGenerator. Each
/// <c>[GenerateInterpolatorSystem]</c> static method emits a full ISystem class with a
/// nested <c>[BurstCompile]</c> InterpolateJob that walks all groups containing the three
/// component triplet and writes the interpolated value, threading dependency tracking
/// through the JobScheduler.
/// </summary>
[TestFixture]
public class InterpolatorJobGeneratorTests
{
    [Test]
    public void Interpolator_GeneratesValidSystem()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public static class Registrations
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                    internal static void InterpolatePos(CPos prev, CPos current, ref CPos interp, float percent) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new InterpolatorJobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(run.GeneratedTrees, Is.Not.Empty);
    }

    [Test]
    public void Interpolator_DifferentNamespaces_CompilesCleanly()
    {
        // Two interpolators in different namespaces — verifies the per-method emission
        // path doesn't accidentally cross namespaces or collide on type names.
        const string source = """
            namespace Sample.A
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public static class RegistrationsA
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                    internal static void InterpolatePos(CPos prev, CPos current, ref CPos interp, float percent) { }
                }
            }

            namespace Sample.B
            {
                public partial struct CRot : Trecs.IEntityComponent { public float W; }

                public static class RegistrationsB
                {
                    [Trecs.GenerateInterpolatorSystem("RotInterpolationSystem", "Movement")]
                    internal static void InterpolateRot(CRot prev, CRot current, ref CRot interp, float percent) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new InterpolatorJobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void Interpolator_NestedRegistrationClass_CompilesCleanly()
    {
        // The generated system class lands at namespace scope and calls back into the
        // user's interpolator method by full path. Confirms a nested registration class
        // (Outer.InnerRegistrations.InterpolatePos) doesn't break that call site.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial class Outer
                {
                    public static partial class InnerRegistrations
                    {
                        [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                        internal static void InterpolatePos(CPos prev, CPos current, ref CPos interp, float percent) { }
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new InterpolatorJobGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void Interpolator_PairedWithInstaller_CompilesCleanly()
    {
        // The two interpolator generators are designed to compose: InterpolatorJob emits
        // the system class, InterpolatorInstaller emits the WorldBuilder extension that
        // registers it. Catches a regression in either generator that would cause symbol
        // mismatches between the two.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public static class Registrations
                {
                    [Trecs.GenerateInterpolatorSystem("PosInterpolationSystem", "Movement")]
                    internal static void InterpolatePos(CPos prev, CPos current, ref CPos interp, float percent) { }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(
            new Microsoft.CodeAnalysis.IIncrementalGenerator[]
            {
                new InterpolatorJobGenerator(),
                new InterpolatorInstallerGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }
}
