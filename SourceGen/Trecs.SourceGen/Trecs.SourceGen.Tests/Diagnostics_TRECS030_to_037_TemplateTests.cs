using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Template diagnostics group (TRECS030-037). All emitted from
/// IncrementalTemplateDefinitionGenerator's TemplateValidator pass when an
/// ITemplate-implementing class violates a template-shape rule.
///
/// Codes covered: 030, 031, 033, 034, 036.
///
/// Codes intentionally not covered here:
/// - TRECS032 (TemplateInvalidAttributeCombination): triggers require the
///   [Interpolated] / [Constant] / [FixedUpdateOnly] / [VariableUpdateOnly] /
///   [Input] attribute stubs in TrecsStubs.cs, which are not currently included.
///   Adding them is its own scope-of-work; deferred.
/// - TRECS037 (GlobalsTemplateFieldMustHaveDefault): triggers require the
///   IHasTags<TrecsTags.Globals> chain stub, also missing. Deferred.
/// - TRECS035: gap in numbering (no descriptor).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS030_to_037_TemplateTests
{
    [Test]
    public void TRECS030_TemplateNotPartial()
    {
        // ITemplate-implementing class must be partial — the generator emits a static
        // Template field onto it.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public class PlayerTemplate : Trecs.ITemplate
                {
                    CPos Position;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS030");
    }

    [Test]
    public void TRECS031_TemplateMustBeClass()
    {
        // Templates are classes. A struct implementing ITemplate is rejected.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial struct PlayerTemplate : Trecs.ITemplate
                {
                    CPos Position;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS031");
    }

    [Test]
    public void TRECS033_TemplateFieldMustBeEntityComponent()
    {
        // Template instance fields must be IEntityComponent — otherwise the field
        // doesn't represent a runtime component slot.
        const string source = """
            namespace Sample
            {
                public partial struct NotAComponent { public int V; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    NotAComponent Junk;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS033");
    }

    [Test]
    public void TRECS034_TemplateFieldHasAccessModifier()
    {
        // Template fields are a config DSL, not API surface — they must omit
        // `public`/`private`/etc.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    public CPos Position;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS034");
    }

    [Test]
    public void TRECS036_ComponentWithManagedField()
    {
        // Components must be unmanaged for NativeArray/Burst compatibility — a
        // component with a `string` field violates that.
        const string source = """
            namespace Sample
            {
                public partial struct CName : Trecs.IEntityComponent { public string Value; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CName Name;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS036");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new IncrementalTemplateDefinitionGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }
}
