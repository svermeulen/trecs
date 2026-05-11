using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for TRECS039 — fires at the call site when an abstract template's
/// <c>static Template Template</c> field is passed to
/// <c>WorldBuilder.AddTemplate</c> / <c>AddTemplates</c>. Emitted by
/// <see cref="AddAbstractTemplateAnalyzer"/>; driven through
/// <see cref="GeneratorTestHarness.RunAnalyzers"/>.
///
/// The user-source samples declare the static <c>Template</c> field directly
/// rather than relying on the source generator to emit it. The analyzer only
/// inspects the type and abstractness of the receiver — running the generator
/// is unnecessary for these tests.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS039_AddAbstractTemplateTests
{
    [Test]
    public void TRECS039_AddTemplate_AbstractTemplate_Fires()
    {
        const string source = """
            namespace Sample
            {
                public abstract class AbsT
                {
                    public static readonly Trecs.Template Template = null;
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var wb = new Trecs.WorldBuilder();
                        wb.AddTemplate(AbsT.Template);
                    }
                }
            }
            """;

        AssertAnalyzerDiagnostic(source, "TRECS039");
    }

    [Test]
    public void TRECS039_AddTemplate_ConcreteTemplate_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class ConT
                {
                    public static readonly Trecs.Template Template = null;
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var wb = new Trecs.WorldBuilder();
                        wb.AddTemplate(ConT.Template);
                    }
                }
            }
            """;

        AssertNoAnalyzerDiagnostic(source, "TRECS039");
    }

    [Test]
    public void TRECS039_AddTemplates_AbstractInArrayLiteral_Fires()
    {
        const string source = """
            namespace Sample
            {
                public abstract class AbsT
                {
                    public static readonly Trecs.Template Template = null;
                }
                public class ConT
                {
                    public static readonly Trecs.Template Template = null;
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var wb = new Trecs.WorldBuilder();
                        wb.AddTemplates(new[] { ConT.Template, AbsT.Template });
                    }
                }
            }
            """;

        AssertAnalyzerDiagnostic(source, "TRECS039");
    }

    [Test]
    public void TRECS039_AddTemplates_ConcreteOnly_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class ConT
                {
                    public static readonly Trecs.Template Template = null;
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var wb = new Trecs.WorldBuilder();
                        wb.AddTemplates(new[] { ConT.Template });
                    }
                }
            }
            """;

        AssertNoAnalyzerDiagnostic(source, "TRECS039");
    }

    [Test]
    public void TRECS039_AddTemplate_LocalVariable_DoesNotFireAtAnalyzer()
    {
        // The analyzer intentionally does NOT flow-trace through locals; this case
        // is covered by the runtime Require.That guard in WorldBuilder.AddTemplate.
        const string source = """
            namespace Sample
            {
                public abstract class AbsT
                {
                    public static readonly Trecs.Template Template = null;
                }

                public static class Caller
                {
                    public static void Go()
                    {
                        var wb = new Trecs.WorldBuilder();
                        var t = AbsT.Template;
                        wb.AddTemplate(t);
                    }
                }
            }
            """;

        AssertNoAnalyzerDiagnostic(source, "TRECS039");
    }

    static void AssertAnalyzerDiagnostic(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new AddAbstractTemplateAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{FormatDiagnostics(diagnostics)}"
        );
    }

    static void AssertNoAnalyzerDiagnostic(string source, string forbiddenId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new AddAbstractTemplateAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == forbiddenId);
        Assert.That(
            diag,
            Is.Null,
            $"Did not expect {forbiddenId}, got:\n{FormatDiagnostics(diagnostics)}"
        );
    }

    static string FormatDiagnostics(
        System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics
    )
    {
        if (diagnostics.IsEmpty)
            return "  (none)";
        return string.Join(
            "\n",
            diagnostics.Select(d =>
                $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
            )
        );
    }
}
