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
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create provider for struct types implementing IEntityComponent
            var entityComponentProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsStructDeclaration(s),
                    transform: static (ctx, _) => GetEntityComponentData(ctx)
                )
                .Where(static m => m is not null);
            var entityComponentProvider = AssemblyFilterHelper.FilterByTrecsReference(
                entityComponentProviderRaw,
                hasTrecsReference
            );

            // Combine with compilation provider
            var entityComponentWithCompilation = entityComponentProvider.Combine(
                context.CompilationProvider
            );

            // Register source output
            context.RegisterSourceOutput(
                entityComponentWithCompilation,
                static (spc, source) =>
                    GenerateEntityComponentSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsStructDeclaration(SyntaxNode node)
        {
            return node is StructDeclarationSyntax;
        }

        private static EntityComponentData? GetEntityComponentData(GeneratorSyntaxContext context)
        {
            var structDecl = (StructDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl);

            if (
                symbol != null
                && SymbolAnalyzer.ImplementsInterface(
                    symbol,
                    "IEntityComponent",
                    TrecsNamespaces.Trecs
                )
            )
            {
                SourceGenLogger.Log(
                    $"[IncrementalEntityComponentGenerator] Found struct with base list: {symbol.Name}"
                );
                return new EntityComponentData(structDecl, symbol);
            }

            return null;
        }

        private static void GenerateEntityComponentSource(
            SourceProductionContext context,
            EntityComponentData data,
            Compilation compilation
        )
        {
            var location = data.StructDecl.GetLocation();
            var symbol = data.Symbol;
            var structDecl = data.StructDecl;

            try
            {
                using var _timer_ = SourceGenTimer.Time("EntityComponentGenerator.Total");
                SourceGenLogger.Log(
                    $"[IncrementalEntityComponentGenerator] Processing {symbol.Name}"
                );

                // Make sure the type is a struct
                if (symbol.TypeKind != TypeKind.Struct)
                {
                    SourceGenLogger.Log(
                        $"[IncrementalEntityComponentGenerator] Type {symbol.Name} implements IEntityComponent but is not a struct"
                    );
                    return;
                }

                // Make sure it's declared as partial
                if (!IsPartialType(structDecl))
                {
                    SourceGenLogger.Log(
                        $"[IncrementalEntityComponentGenerator] Type {symbol.Name} is not declared as partial, generated code may cause errors"
                    );
                }

                // Generate the equality and operator methods
                var source = GenerateComponentCode(symbol);
                var fileName = SymbolAnalyzer.GetSafeFileName(symbol);

                context.AddSource(fileName, source);
                SourceGenLogger.WriteGeneratedFile(fileName, source);
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[IncrementalEntityComponentGenerator] Error generating code for {symbol.Name}: {ex.Message}"
                );

                // Report error for any unhandled exceptions
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    location,
                    $"{symbol.Name}: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        /// <summary>
        /// Checks if a type is declared with the partial modifier
        /// </summary>
        private static bool IsPartialType(TypeDeclarationSyntax typeDeclaration)
        {
            return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        }

        /// <summary>
        /// Generates the source code for the component's equality and operator methods
        /// </summary>
        private static string GenerateComponentCode(INamedTypeSymbol symbol)
        {
            var namespaceName = PerformanceCache.GetDisplayString(symbol.ContainingNamespace);
            var typeName = symbol.Name;
            var containingTypes = GetContainingTypeChain(symbol);

            // Get the accessibility of the original type
            var accessibility = GetAccessibilityModifier(symbol);

            // Check if this is a UnwrapComponent
            var isUnwrapComponent = PerformanceCache.HasAttributeByName(
                symbol,
                TrecsAttributeNames.Unwrap,
                TrecsNamespaces.Trecs
            );

            var sb = new StringBuilder();

            // Add using statements
            sb.AppendLine("using System;");
            sb.AppendLine("using Trecs;");
            sb.AppendLine("using Trecs.Internal;");
            sb.AppendLine("using Trecs.Collections;");
            sb.AppendLine();

            // Start namespace (if not global)
            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            // Open nested containing types if needed
            int indentLevel = string.IsNullOrEmpty(namespaceName) ? 0 : 1;
            foreach (var containingType in containingTypes)
            {
                var indent = new string(' ', indentLevel * 4);
                var kind = containingType.Item2 == TypeKind.Class ? "class" : "struct";
                var containerAccessibility = containingType.Item3;

                sb.AppendLine(
                    $"{indent}{containerAccessibility} partial {kind} {containingType.Item1}"
                );
                sb.AppendLine($"{indent}{{");
                indentLevel++;
            }

            // Start struct declaration
            var structIndent = new string(' ', indentLevel * 4);
            sb.AppendLine($"{structIndent}{accessibility} partial struct {typeName}");
            sb.AppendLine($"{structIndent}{{");
            indentLevel++;

            var methodIndent = new string(' ', indentLevel * 4);

            // Add constructor for UnwrapComponent
            if (isUnwrapComponent)
            {
                // Get the single field from the component
                var field = symbol
                    .GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => !f.IsStatic && !f.IsConst)
                    .FirstOrDefault();

                if (field != null)
                {
                    // Check if constructor already exists
                    var hasConstructor = symbol.Constructors.Any(c =>
                        c.Parameters.Length == 1
                        && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, field.Type)
                    );

                    if (!hasConstructor)
                    {
                        // Generate constructor that takes the value
                        sb.AppendLine(
                            $"{methodIndent}public {typeName}({PerformanceCache.GetDisplayString(field.Type)} value)"
                        );
                        sb.AppendLine($"{methodIndent}{{");
                        sb.AppendLine($"{methodIndent}    this.{field.Name} = value;");
                        sb.AppendLine($"{methodIndent}}}");
                        sb.AppendLine();
                    }
                }
            }

            // Add Equals override
            sb.AppendLine($"{methodIndent}public override bool Equals(object obj)");
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    if (obj is {typeName} other)");
            sb.AppendLine($"{methodIndent}    {{");
            sb.AppendLine(
                $"{methodIndent}        return UnmanagedUtil.BlittableEquals(this, other);"
            );
            sb.AppendLine($"{methodIndent}    }}");
            sb.AppendLine($"{methodIndent}    return false;");
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            // Add GetHashCode override
            sb.AppendLine($"{methodIndent}public override readonly int GetHashCode()");
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    throw new NotImplementedException(");
            sb.AppendLine(
                $"{methodIndent}        \"GetHashCode not supported for IEntityComponent derived types\");"
            );
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            // Add == operator
            sb.AppendLine(
                $"{methodIndent}public static bool operator ==(in {typeName} left, in {typeName} right)"
            );
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    return UnmanagedUtil.BlittableEquals(left, right);");
            sb.AppendLine($"{methodIndent}}}");
            sb.AppendLine();

            // Add != operator
            sb.AppendLine(
                $"{methodIndent}public static bool operator !=(in {typeName} left, in {typeName} right)"
            );
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{methodIndent}    return !UnmanagedUtil.BlittableEquals(left, right);");
            sb.AppendLine($"{methodIndent}}}");

            // Close all braces
            while (indentLevel > 0)
            {
                indentLevel--;
                sb.AppendLine($"{new string(' ', indentLevel * 4)}}}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets the accessibility modifier for a symbol as a string
        /// </summary>
        private static string GetAccessibilityModifier(ISymbol symbol)
        {
            switch (symbol.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    return "public";
                case Accessibility.Internal:
                    return "internal";
                case Accessibility.Private:
                    return "private";
                case Accessibility.Protected:
                    return "protected";
                case Accessibility.ProtectedOrInternal:
                    return "protected internal";
                case Accessibility.ProtectedAndInternal:
                    return "private protected";
                default:
                    return "internal"; // Default to internal if not specified
            }
        }

        /// <summary>
        /// Gets the chain of containing types for a nested type
        /// </summary>
        private static List<(string, TypeKind, string)> GetContainingTypeChain(
            INamedTypeSymbol symbol
        )
        {
            var result = new List<(string, TypeKind, string)>();
            var current = symbol.ContainingType;

            while (current != null)
            {
                result.Add((current.Name, current.TypeKind, GetAccessibilityModifier(current)));
                current = current.ContainingType;
            }

            result.Reverse();
            return result;
        }
    }

    /// <summary>
    /// Data structure for entity component information used in incremental generation
    /// </summary>
    internal class EntityComponentData
    {
        public StructDeclarationSyntax StructDecl { get; }
        public INamedTypeSymbol Symbol { get; }

        public EntityComponentData(StructDeclarationSyntax structDecl, INamedTypeSymbol symbol)
        {
            StructDecl = structDecl;
            Symbol = symbol;
        }
    }
}
