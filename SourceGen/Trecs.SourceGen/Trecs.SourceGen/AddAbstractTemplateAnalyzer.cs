#nullable enable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Catches calls to <c>WorldBuilder.AddTemplate(...)</c> / <c>AddTemplates(...)</c>
    /// that pass an abstract template's static <c>Template</c> field — e.g.
    /// <c>builder.AddTemplate(AbstractFoo.Template)</c>. Abstract templates exist
    /// only as <c>IExtends&lt;&gt;</c> bases.
    ///
    /// Scope is intentionally narrow: only the direct <c>FooT.Template</c> field-reference
    /// form is detected. Locals (<c>var t = FooT.Template; builder.AddTemplate(t);</c>),
    /// ternaries, method returns, and other indirect forms are NOT flow-traced — they fall
    /// through to the runtime <c>Require.That</c> guard in <c>WorldBuilder.AddTemplate</c>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AddAbstractTemplateAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(DiagnosticDescriptors.AddAbstractTemplate);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;

            if (method.Name != "AddTemplate" && method.Name != "AddTemplates")
                return;

            var containingType = method.ContainingType;
            if (containingType == null)
                return;
            if (containingType.Name != "WorldBuilder")
                return;
            if (containingType.ContainingNamespace?.ToDisplayString() != "Trecs")
                return;

            if (invocation.Arguments.Length == 0)
                return;

            var arg = invocation.Arguments[0].Value;

            if (method.Name == "AddTemplate")
            {
                ReportIfAbstract(context, arg);
                return;
            }

            // AddTemplates(IEnumerable<Template>) — only inspect statically-visible array
            // / collection literals. Anything else falls through to the runtime guard.
            foreach (var element in EnumerateArrayElements(arg))
            {
                ReportIfAbstract(context, element);
            }
        }

        private static System.Collections.Generic.IEnumerable<IOperation> EnumerateArrayElements(
            IOperation operation
        )
        {
            var unwrapped = Unwrap(operation);
            if (
                unwrapped is IArrayCreationOperation arrayCreation
                && arrayCreation.Initializer != null
            )
            {
                foreach (var element in arrayCreation.Initializer.ElementValues)
                {
                    yield return element;
                }
            }
            // Collection-expression / list / etc. fall through to the runtime backstop.
        }

        private static void ReportIfAbstract(OperationAnalysisContext context, IOperation argument)
        {
            var unwrapped = Unwrap(argument);
            if (unwrapped is not IFieldReferenceOperation fieldRef)
                return;

            var field = fieldRef.Field;
            if (!field.IsStatic || field.Name != "Template")
                return;

            // Guard against unrelated types that happen to have a `static Template` member.
            var fieldType = field.Type as INamedTypeSymbol;
            if (fieldType?.Name != "Template")
                return;
            if (fieldType.ContainingNamespace?.ToDisplayString() != "Trecs")
                return;

            var declaringType = field.ContainingType;
            if (declaringType == null || !declaringType.IsAbstract)
                return;

            var diagnostic = Diagnostic.Create(
                DiagnosticDescriptors.AddAbstractTemplate,
                argument.Syntax.GetLocation(),
                declaringType.Name
            );

            context.ReportDiagnostic(diagnostic);
        }

        private static IOperation Unwrap(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }
            return operation;
        }
    }
}
