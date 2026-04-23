using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;

// ReSharper disable MemberCanBePrivate.Global

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Utility class for common symbol analysis operations used across generators
    /// </summary>
    internal static class SymbolAnalyzer
    {
        /// <summary>
        /// Checks if a type is declared with the partial modifier
        /// </summary>
        public static bool IsPartialType(TypeDeclarationSyntax typeDeclaration)
        {
            return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        }

        /// <summary>
        /// Checks if a type implements a specific interface by name and namespace.
        /// </summary>
        public static bool ImplementsInterface(
            ITypeSymbol type,
            string interfaceName,
            string namespaceName
        )
        {
            if (type is not INamedTypeSymbol namedType)
                return false;

            foreach (var iface in namedType.AllInterfaces)
            {
                if (
                    iface.Name == interfaceName
                    && IsInNamespace(iface.ContainingNamespace, namespaceName)
                )
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is an aspect interface — i.e. an interface
        /// (not a struct/class) that extends <c>Trecs.IAspect</c> directly or transitively, and
        /// is not <c>Trecs.IAspect</c> itself. Aspect interfaces are the shared-contract flavor
        /// of aspects: a generic helper constrained on one compiles against any concrete aspect
        /// that implements it.
        /// </summary>
        public static bool IsAspectInterface(ITypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Interface)
                return false;

            // Exclude Trecs.IAspect itself — it's the marker, not an aspect interface.
            if (IsExactType(type, "IAspect", TrecsNamespaces.Trecs))
                return false;

            return ImplementsInterface(type, "IAspect", TrecsNamespaces.Trecs);
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is exactly the named type in the given namespace.
        /// Robust against user-defined types of the same name in other namespaces.
        /// </summary>
        public static bool IsExactType(ITypeSymbol type, string typeName, string namespaceName)
        {
            return type.Name == typeName
                && PerformanceCache.GetDisplayString(type.ContainingNamespace) == namespaceName;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is <c>SetAccessor&lt;T&gt;</c> from the Trecs namespace.
        /// </summary>
        public static bool IsSetAccessorType(ITypeSymbol type)
        {
            return type is INamedTypeSymbol named
                && named.Name == "SetAccessor"
                && named.TypeArguments.Length == 1
                && PerformanceCache.GetDisplayString(named.ContainingNamespace)
                    == TrecsNamespaces.Trecs;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is <c>SetRead&lt;T&gt;</c> or <c>SetWrite&lt;T&gt;</c> from the Trecs namespace.
        /// </summary>
        public static bool IsSetReadOrWriteType(ITypeSymbol type)
        {
            return type is INamedTypeSymbol named
                && (named.Name == "SetRead" || named.Name == "SetWrite")
                && named.TypeArguments.Length == 1
                && PerformanceCache.GetDisplayString(named.ContainingNamespace)
                    == TrecsNamespaces.Trecs;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is a loop-managed parameter type that the
        /// iteration generators handle internally (EntityIndex, WorldAccessor, SetAccessor&lt;T&gt;,
        /// SetRead&lt;T&gt;, SetWrite&lt;T&gt;).
        /// These should not be treated as custom pass-through arguments.
        /// </summary>
        public static bool IsLoopManagedType(ITypeSymbol type)
        {
            return IsExactType(type, "EntityIndex", TrecsNamespaces.Trecs)
                || IsExactType(type, "WorldAccessor", TrecsNamespaces.Trecs)
                || IsSetAccessorType(type)
                || IsSetReadOrWriteType(type);
        }

        /// <summary>
        /// Creates a valid, unique filename for generated code
        /// </summary>
        public static string GetSafeFileName(INamedTypeSymbol symbol, string suffix = "Generated")
        {
            // Handle nested types by including containing type names
            var parts = new List<string>();
            var current = symbol;

            while (current != null)
            {
                parts.Add(current.Name);
                current = current.ContainingType;
            }

            parts.Reverse();
            string typePath = string.Join(".", parts);

            // Add namespace for uniqueness
            string namespaceName = PerformanceCache.GetDisplayString(symbol.ContainingNamespace);
            if (!string.IsNullOrEmpty(namespaceName))
            {
                return $"{namespaceName}.{typePath}.{suffix}.g.cs";
            }

            return $"{typePath}.{suffix}.g.cs";
        }

        /// <summary>
        /// Gets the full namespace chain for a symbol
        /// </summary>
        public static string GetNamespaceChain(ISymbol symbol)
        {
            var namespaces = new List<string>();
            var current = symbol.ContainingNamespace;

            while (current != null && !current.IsGlobalNamespace)
            {
                namespaces.Add(current.Name);
                current = current.ContainingNamespace;
            }

            namespaces.Reverse();
            return string.Join(".", namespaces);
        }

        /// <summary>
        /// Gets the containing type chain for nested types
        /// </summary>
        public static List<string> GetContainingTypeChain(INamedTypeSymbol symbol)
        {
            var parts = new List<string>();
            var current = symbol.ContainingType;

            while (current != null)
            {
                parts.Add(current.Name);
                current = current.ContainingType;
            }

            parts.Reverse();
            return parts;
        }

        /// <summary>
        /// Determines the accessibility modifier for a symbol
        /// </summary>
        public static string GetAccessibilityModifier(ISymbol symbol)
        {
            return symbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                Accessibility.Protected => "protected",
                Accessibility.Private => "private",
                _ => "internal",
            };
        }

        /// <summary>
        /// Checks if a type implements a specific interface, optionally verifying the namespace
        /// </summary>
        public static bool ImplementsInterface(
            INamedTypeSymbol type,
            string interfaceName,
            string? namespaceName = null
        )
        {
            return type.AllInterfaces.Any(i =>
                i.Name == interfaceName
                && (namespaceName == null || IsInNamespace(i.ContainingNamespace, namespaceName))
            );
        }

        /// <summary>
        /// Validates that a symbol is not null and reports diagnostic if it is
        /// </summary>
        public static bool ValidateSymbolNotNull(
            ISymbol symbol,
            string symbolName,
            Location location,
            Action<Diagnostic> reportDiagnostic
        )
        {
            if (symbol == null)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    location,
                    symbolName
                );
                reportDiagnostic(diagnostic);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the attribute data for a specific attribute type, optionally verifying the namespace
        /// </summary>
        public static AttributeData? GetAttributeData(
            ISymbol symbol,
            string attributeName,
            string? namespaceName = null
        )
        {
            return PerformanceCache
                .GetAttributes(symbol)
                .FirstOrDefault(attr =>
                    attr.AttributeClass?.Name == attributeName
                    && (
                        namespaceName == null
                        || IsInNamespace(attr.AttributeClass?.ContainingNamespace, namespaceName)
                    )
                );
        }

        /// <summary>
        /// Checks if a namespace symbol matches a simple (non-nested) namespace name
        /// without allocating a display string. For nested namespaces like "A.B",
        /// falls back to ToDisplayString comparison.
        /// </summary>
        public static bool IsInNamespace(INamespaceSymbol? ns, string namespaceName)
        {
            if (ns == null)
                return false;
            // Fast path for simple namespace (e.g. "Trecs")
            if (!namespaceName.Contains("."))
            {
                return ns.Name == namespaceName
                    && ns.ContainingNamespace?.IsGlobalNamespace == true;
            }
            // Fallback for nested namespaces
            return PerformanceCache.GetDisplayString(ns) == namespaceName;
        }

        /// <summary>
        /// Gets the namespace of a class declaration by walking up to the nearest
        /// <see cref="BaseNamespaceDeclarationSyntax"/> parent.
        /// </summary>
        public static string GetNamespace(ClassDeclarationSyntax classNode)
        {
            var namespaceDecl = classNode.Parent as BaseNamespaceDeclarationSyntax;
            return namespaceDecl?.Name.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Gets the visibility modifier string for a method declaration syntax node.
        /// Returns "private" when no explicit modifier is present (C# default).
        /// </summary>
        public static string GetMethodVisibility(MethodDeclarationSyntax methodDecl)
        {
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                return "public";
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)))
            {
                if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                    return "protected internal";
                return "protected";
            }
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                return "internal";
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))
                return "private";

            // Default to private if no visibility modifier is specified (C# default)
            return "private";
        }
    }
}
