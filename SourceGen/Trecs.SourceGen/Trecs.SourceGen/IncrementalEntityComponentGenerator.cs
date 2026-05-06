#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that adds required equality and operator methods to all types
    /// implementing IEntityComponent. Provides better compilation performance than the legacy EntityComponentGenerator.
    /// </summary>
    [Generator]
    public class IncrementalEntityComponentGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Extract a value-equality EntityComponentModel in the transform so the
            // Roslyn incremental pipeline can actually cache across edits. Storing
            // SyntaxNode / ISymbol in pipeline state defeats caching because those
            // types use reference equality.
            var modelProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => s is StructDeclarationSyntax,
                    transform: static (ctx, _) => ExtractModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            var modelProvider = AssemblyFilterHelper.FilterByTrecsReference(
                modelProviderRaw,
                hasTrecsReference
            );

            context.RegisterSourceOutput(modelProvider, static (spc, m) => Generate(spc, m));
        }

        static EntityComponentModel? ExtractModel(GeneratorSyntaxContext context)
        {
            var structDecl = (StructDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl);

            if (
                symbol is null
                || symbol.TypeKind != TypeKind.Struct
                || !SymbolAnalyzer.ImplementsInterface(
                    symbol,
                    "IEntityComponent",
                    TrecsNamespaces.Trecs
                )
            )
            {
                return null;
            }

            var isPartial = structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
            var isUnwrap = PerformanceCache.HasAttributeByName(
                symbol,
                TrecsAttributeNames.Unwrap,
                TrecsNamespaces.Trecs
            );
            // [Serializable] has AllowMultiple=false, so applying it twice
            // (once by the user, once by us) is a CS0579 error. Skip emission
            // when the user already wrote it on any partial half.
            var isSerializable = PerformanceCache.HasAttributeByName(
                symbol,
                "SerializableAttribute",
                "System"
            );

            string? unwrapFieldName = null;
            string? unwrapFieldTypeDisplay = null;
            bool hasUnwrapConstructor = false;
            if (isUnwrap)
            {
                var field = symbol
                    .GetMembers()
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault(f => !f.IsStatic && !f.IsConst);
                if (field != null)
                {
                    unwrapFieldName = field.Name;
                    unwrapFieldTypeDisplay = PerformanceCache.GetDisplayString(field.Type);
                    hasUnwrapConstructor = symbol.Constructors.Any(c =>
                        c.Parameters.Length == 1
                        && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, field.Type)
                    );
                }
            }

            var containingTypeChain = SymbolAnalyzer.GetContainingTypeChainInfo(symbol);
            var containingTypes = ImmutableArray.CreateBuilder<ContainingTypeInfo>(
                containingTypeChain.Count
            );
            foreach (var info in containingTypeChain)
            {
                containingTypes.Add(info);
            }

            // Capture the type parameter list (e.g. "<T>" or "<TKey, TValue>")
            // so the emitted partial matches the original declaration —
            // partial declarations are matched on the full name including
            // arity + parameter names, so dropping "<T>" silently emits a
            // phantom non-generic type that nothing merges into. Empty for
            // non-generic structs.
            string typeParameterList = SymbolAnalyzer.FormatTypeParameterList(symbol);

            return new EntityComponentModel(
                TypeName: symbol.Name,
                TypeParameterList: typeParameterList,
                Namespace: PerformanceCache.GetDisplayString(symbol.ContainingNamespace),
                Accessibility: GetAccessibility(symbol),
                IsPartial: isPartial,
                IsUnwrap: isUnwrap,
                IsSerializable: isSerializable,
                UnwrapFieldName: unwrapFieldName,
                UnwrapFieldTypeDisplay: unwrapFieldTypeDisplay,
                HasUnwrapConstructor: hasUnwrapConstructor,
                ContainingTypes: containingTypes.ToImmutable(),
                SafeFileName: SymbolAnalyzer.GetSafeFileName(symbol)
            );
        }

        static void Generate(SourceProductionContext context, EntityComponentModel model)
        {
            try
            {
                using var _timer_ = SourceGenTimer.Time("EntityComponentGenerator.Total");
                SourceGenLogger.Log(
                    $"[IncrementalEntityComponentGenerator] Processing {model.TypeName}"
                );

                if (!model.IsPartial)
                {
                    SourceGenLogger.Log(
                        $"[IncrementalEntityComponentGenerator] Type {model.TypeName} is not declared as partial, generated code may cause errors"
                    );
                }

                var source = GenerateComponentCode(model);
                context.AddSource(model.SafeFileName, source);
                SourceGenLogger.WriteGeneratedFile(model.SafeFileName, source);
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[IncrementalEntityComponentGenerator] Error generating code for {model.TypeName}: {ex.Message}"
                );

                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    Location.None,
                    $"{model.TypeName}: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        static string GenerateComponentCode(EntityComponentModel model)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            CommonUsings.AppendTo(sb);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(model.Namespace))
            {
                sb.AppendLine($"namespace {model.Namespace}");
                sb.AppendLine("{");
            }

            int indentLevel = string.IsNullOrEmpty(model.Namespace) ? 0 : 1;
            foreach (var containingType in model.ContainingTypes)
            {
                var indent = new string(' ', indentLevel * 4);
                sb.AppendLine(
                    $"{indent}{containingType.Accessibility} partial {containingType.Kind} {containingType.Name}{containingType.TypeParameterList}"
                );
                sb.AppendLine($"{indent}{{");
                indentLevel++;
            }

            var structIndent = new string(' ', indentLevel * 4);
            // Emit [Serializable] so Unity's SerializedObject machinery (and
            // any [SerializeField] holding the component) can navigate the
            // type's fields — required for editor inspectors that drive
            // PropertyField. Skipped when the user has already applied it to
            // a hand-written partial; SerializableAttribute has AllowMultiple
            // = false and would otherwise break the build.
            if (!model.IsSerializable)
            {
                sb.AppendLine($"{structIndent}[global::System.Serializable]");
            }
            sb.AppendLine(
                $"{structIndent}{model.Accessibility} partial struct {model.TypeName}{model.TypeParameterList}"
            );
            sb.AppendLine($"{structIndent}{{");
            indentLevel++;

            var methodIndent = new string(' ', indentLevel * 4);

            if (
                model.IsUnwrap
                && model.UnwrapFieldName != null
                && model.UnwrapFieldTypeDisplay != null
                && !model.HasUnwrapConstructor
            )
            {
                sb.AppendLine(
                    $"{methodIndent}public {model.TypeName}({model.UnwrapFieldTypeDisplay} value)"
                );
                sb.AppendLine($"{methodIndent}{{");
                sb.AppendLine($"{methodIndent}    this.{model.UnwrapFieldName} = value;");
                sb.AppendLine($"{methodIndent}}}");
                sb.AppendLine();
            }

            sb.AppendLine($"{methodIndent}public override bool Equals(object obj)");
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine(
                $"{methodIndent}    if (obj is {model.TypeName}{model.TypeParameterList} other)"
            );
            sb.AppendLine($"{methodIndent}    {{");
            sb.AppendLine(
                $"{methodIndent}        return UnmanagedUtil.BlittableEquals(this, other);"
            );
            sb.AppendLine($"{methodIndent}    }}");
            sb.AppendLine($"{methodIndent}    return false;");
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            sb.AppendLine($"{methodIndent}public override readonly int GetHashCode()");
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    throw new NotImplementedException(");
            sb.AppendLine(
                $"{methodIndent}        \"GetHashCode not supported for IEntityComponent derived types\");"
            );
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            sb.AppendLine(
                $"{methodIndent}public static bool operator ==(in {model.TypeName}{model.TypeParameterList} left, in {model.TypeName}{model.TypeParameterList} right)"
            );
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    return UnmanagedUtil.BlittableEquals(left, right);");
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            sb.AppendLine(
                $"{methodIndent}public static bool operator !=(in {model.TypeName}{model.TypeParameterList} left, in {model.TypeName}{model.TypeParameterList} right)"
            );
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    return !UnmanagedUtil.BlittableEquals(left, right);");
            sb.AppendLine($"{methodIndent}}}");

            while (indentLevel > 0)
            {
                indentLevel--;
                sb.AppendLine($"{new string(' ', indentLevel * 4)}}}");
            }

            return sb.ToString();
        }

        static string GetAccessibility(ISymbol symbol) =>
            symbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => "internal",
            };
    }

    /// <summary>
    /// Value-equality model carried through the incremental pipeline so Roslyn
    /// can cache generator output. All fields are primitives or
    /// <see cref="ImmutableArray{T}"/> of value-equality records — no Roslyn
    /// symbol / syntax references.
    /// </summary>
    internal readonly record struct EntityComponentModel(
        string TypeName,
        string TypeParameterList,
        string Namespace,
        string Accessibility,
        bool IsPartial,
        bool IsUnwrap,
        bool IsSerializable,
        string? UnwrapFieldName,
        string? UnwrapFieldTypeDisplay,
        bool HasUnwrapConstructor,
        ImmutableArray<ContainingTypeInfo> ContainingTypes,
        string SafeFileName
    )
    {
        public bool Equals(EntityComponentModel other) =>
            TypeName == other.TypeName
            && TypeParameterList == other.TypeParameterList
            && Namespace == other.Namespace
            && Accessibility == other.Accessibility
            && IsPartial == other.IsPartial
            && IsUnwrap == other.IsUnwrap
            && IsSerializable == other.IsSerializable
            && UnwrapFieldName == other.UnwrapFieldName
            && UnwrapFieldTypeDisplay == other.UnwrapFieldTypeDisplay
            && HasUnwrapConstructor == other.HasUnwrapConstructor
            && SafeFileName == other.SafeFileName
            && ContainingTypes.SequenceEqual(other.ContainingTypes);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (TypeName?.GetHashCode() ?? 0);
                h = h * 31 + (TypeParameterList?.GetHashCode() ?? 0);
                h = h * 31 + (Namespace?.GetHashCode() ?? 0);
                h = h * 31 + (Accessibility?.GetHashCode() ?? 0);
                h = h * 31 + IsPartial.GetHashCode();
                h = h * 31 + IsUnwrap.GetHashCode();
                h = h * 31 + IsSerializable.GetHashCode();
                h = h * 31 + (UnwrapFieldName?.GetHashCode() ?? 0);
                h = h * 31 + (UnwrapFieldTypeDisplay?.GetHashCode() ?? 0);
                h = h * 31 + HasUnwrapConstructor.GetHashCode();
                h = h * 31 + (SafeFileName?.GetHashCode() ?? 0);
                h = h * 31 + ContainingTypes.Length;
                return h;
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // Polyfill required for C# records on netstandard2.0 target.
    internal static class IsExternalInit { }
}
