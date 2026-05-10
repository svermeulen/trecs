using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Template diagnostics group (TRECS030-037). All emitted from
/// TemplateDefinitionGenerator's TemplateValidator pass when an
/// ITemplate-implementing class violates a template-shape rule, plus TRECS035
/// emitted from VariableUpdateOnlyValidator for misapplied
/// [VariableUpdateOnly].
///
/// Codes covered: 030, 031, 032, 033, 034, 035, 036, 037.
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
    public void TRECS032_InterpolatedAndConstantOnSameField()
    {
        // [Interpolated] interpolates between snapshots each variable frame;
        // [Constant] makes the value immutable. The two are mutually exclusive.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Interpolated]
                    [Trecs.Constant]
                    CPos Position;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS032");
    }

    [Test]
    public void TRECS032_InputAndVariableUpdateOnlyOnSameField()
    {
        // [Input] components are written by Input-phase systems and read on
        // FixedUpdate; [VariableUpdateOnly] restricts a component to the
        // variable-update phase. Combining them is incoherent.
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent { public int V; }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    [Trecs.VariableUpdateOnly]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS032");
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
    public void TRECS035_VariableUpdateOnlyOnPlainClass()
    {
        // [VariableUpdateOnly] is silently ignored on a class that doesn't
        // implement ITemplate — flag at compile time so the mistake doesn't
        // slip through. (Struct misuse is caught by C# CS0592, since the
        // attribute doesn't allow AttributeTargets.Struct.)
        const string source = """
            namespace Sample
            {
                [Trecs.VariableUpdateOnly]
                public class JustSomeService { }
            }
            """;

        AssertDiagnostic(source, "TRECS035");
    }

    [Test]
    public void TRECS035_DoesNotFireOnTemplate()
    {
        // Positive control: a template class is the canonical valid target;
        // no TRECS035 diagnostic must be emitted.
        const string source = """
            namespace Sample
            {
                [Trecs.VariableUpdateOnly]
                public partial class RenderTemplate : Trecs.ITemplate { }
            }
            """;

        AssertNoDiagnostic(source, "TRECS035");
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

    [Test]
    public void TRECS037_GlobalsTemplateFieldMissingDefault()
    {
        // The globals entity is created automatically; there's no opportunity to
        // pass field initializers via a template instance, so every component
        // field on a Globals-tagged template must carry an explicit `= default;`.
        const string source = """
            namespace Sample
            {
                public partial struct CScore : Trecs.IEntityComponent { public int V; }

                public partial class GameGlobalsTemplate
                    : Trecs.ITemplate, Trecs.ITagged<Trecs.TrecsTags.Globals>
                {
                    CScore Score;  // No `= default;` — should fire TRECS037.
                }
            }
            """;

        AssertDiagnostic(source, "TRECS037");
    }

    [Test]
    public void TRECS037_DoesNotFireOnGlobalsFieldWithDefault()
    {
        // Positive control: a Globals template with the required `= default;`
        // initializer must not trip TRECS037.
        const string source = """
            namespace Sample
            {
                public partial struct CScore : Trecs.IEntityComponent { public int V; }

                public partial class GameGlobalsTemplate
                    : Trecs.ITemplate, Trecs.ITagged<Trecs.TrecsTags.Globals>
                {
                    CScore Score = default;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS037");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
                new VariableUpdateOnlyValidator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }

    static void AssertNoDiagnostic(string source, string forbiddenId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
                new VariableUpdateOnlyValidator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == forbiddenId);
        Assert.That(diag, Is.Null, $"Did not expect {forbiddenId}, got:\n{run.Format()}");
    }
}
