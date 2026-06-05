#nullable enable

using System;
using System.Collections.Generic;
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
    /// Incremental source generator for the <c>[CascadeRemove]</c> /
    /// <c>[DisposeOnRemove]</c> feature. For every <c>IEntityComponent</c> struct
    /// that carries at least one annotated field, it emits a partial that
    /// explicitly implements <c>Trecs.IComponentRemovalHandlers</c>, registering
    /// two kinds of per-field handlers into the framework's
    /// <c>RemovalHandlerCollector</c> at world build:
    /// <list type="bullet">
    ///   <item><description><b>cascade-read</b> handlers (from
    ///     <c>[CascadeRemove]</c>) read the referenced <c>EntityHandle</c>(s) and
    ///     queue their removal — run in the removal read phase alongside user
    ///     <c>OnRemoved</c>;</description></item>
    ///   <item><description><b>dispose</b> handlers (from <c>[DisposeOnRemove]</c>)
    ///     free the heap-backed field — run strictly afterward in the dispose
    ///     phase.</description></item>
    /// </list>
    /// The emitted handler bodies use only the public
    /// <c>world.ComponentBuffer&lt;T&gt;(group).Read</c> surface (the same channel
    /// the <c>[ForEachEntity]</c> OnRemoved range overload uses), so they compile
    /// in the user's assembly. The dispatch from core to these handlers rides
    /// <c>ResolvedComponentDeclaration&lt;T&gt;</c> (a <c>default(T) is
    /// IComponentRemovalHandlers</c> test) — no module initializer, no reflection.
    /// </summary>
    [Generator]
    public class RemovalFieldHandlerGenerator : IIncrementalGenerator
    {
        // Disposable field types [DisposeOnRemove] accepts. The generated call is
        // uniform (field.Dispose(world)); each type's own Dispose frees or
        // decrements as appropriate.
        static readonly string[] DisposableTypeNames =
        {
            "TrecsList",
            "UniquePtr",
            "SharedPtr",
            "NativeUniquePtr",
            "NativeSharedPtr",
        };

        const int CascadeShapeNone = 0;
        const int CascadeShapeSingleHandle = 1;
        const int CascadeShapeHandleList = 2;

        const int DiagKindCascade = 1;
        const int DiagKindDispose = 2;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

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

        static RemovalHandlerModel? ExtractModel(GeneratorSyntaxContext context)
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

            List<RemovalFieldInfo>? fields = null;
            List<RemovalDiagnostic>? diagnostics = null;

            foreach (var member in symbol.GetMembers())
            {
                if (member is not IFieldSymbol field || field.IsStatic || field.IsConst)
                {
                    continue;
                }

                bool hasCascade = HasAttribute(field, "CascadeRemoveAttribute");
                bool hasDispose = HasAttribute(field, "DisposeOnRemoveAttribute");

                if (!hasCascade && !hasDispose)
                {
                    continue;
                }

                int cascadeShape = CascadeShapeNone;
                bool disposeValid = false;

                if (hasCascade)
                {
                    cascadeShape = ClassifyCascadeShape(field.Type);
                    if (cascadeShape == CascadeShapeNone)
                    {
                        diagnostics ??= new List<RemovalDiagnostic>();
                        diagnostics.Add(
                            new RemovalDiagnostic(
                                DiagKindCascade,
                                field.Name,
                                PerformanceCache.GetDisplayString(field.Type),
                                LocationInfo.From(GetFieldLocation(field))
                            )
                        );
                    }
                }

                if (hasDispose)
                {
                    disposeValid = IsDisposableField(field.Type);
                    if (!disposeValid)
                    {
                        diagnostics ??= new List<RemovalDiagnostic>();
                        diagnostics.Add(
                            new RemovalDiagnostic(
                                DiagKindDispose,
                                field.Name,
                                PerformanceCache.GetDisplayString(field.Type),
                                LocationInfo.From(GetFieldLocation(field))
                            )
                        );
                    }
                }

                bool emitCascade = hasCascade && cascadeShape != CascadeShapeNone;
                bool emitDispose = hasDispose && disposeValid;

                if (emitCascade || emitDispose)
                {
                    fields ??= new List<RemovalFieldInfo>();
                    fields.Add(
                        new RemovalFieldInfo(
                            field.Name,
                            emitCascade,
                            emitDispose,
                            emitCascade ? cascadeShape : CascadeShapeNone
                        )
                    );
                }
            }

            if (fields == null && diagnostics == null)
            {
                return null;
            }

            bool isPartial = structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

            return new RemovalHandlerModel(
                TypeName: symbol.Name,
                TypeParameterList: SymbolAnalyzer.FormatTypeParameterList(symbol),
                Namespace: PerformanceCache.GetDisplayString(symbol.ContainingNamespace),
                Accessibility: GetAccessibility(symbol),
                IsPartial: isPartial,
                Fields: (fields ?? new List<RemovalFieldInfo>()).ToEquatableArray(),
                ContainingTypes: SymbolAnalyzer
                    .GetContainingTypeChainInfo(symbol)
                    .ToEquatableArray(),
                SafeFileName: SymbolAnalyzer.GetSafeFileName(symbol, "RemovalHandlers"),
                Diagnostics: (diagnostics ?? new List<RemovalDiagnostic>()).ToEquatableArray()
            );
        }

        static int ClassifyCascadeShape(ITypeSymbol type)
        {
            if (SymbolAnalyzer.IsExactType(type, "EntityHandle", TrecsNamespaces.Trecs))
            {
                return CascadeShapeSingleHandle;
            }

            if (
                type is INamedTypeSymbol named
                && named.Name == "TrecsList"
                && named.TypeArguments.Length == 1
                && PerformanceCache.GetDisplayString(named.ContainingNamespace)
                    == TrecsNamespaces.Trecs
                && SymbolAnalyzer.IsExactType(
                    named.TypeArguments[0],
                    "EntityHandle",
                    TrecsNamespaces.Trecs
                )
            )
            {
                return CascadeShapeHandleList;
            }

            return CascadeShapeNone;
        }

        static bool IsDisposableField(ITypeSymbol type)
        {
            return type is INamedTypeSymbol named
                && named.TypeArguments.Length == 1
                && PerformanceCache.GetDisplayString(named.ContainingNamespace)
                    == TrecsNamespaces.Trecs
                && DisposableTypeNames.Contains(named.Name);
        }

        static bool HasAttribute(IFieldSymbol field, string attributeName)
        {
            foreach (var attr in field.GetAttributes())
            {
                var cls = attr.AttributeClass;
                if (
                    cls != null
                    && cls.Name == attributeName
                    && PerformanceCache.GetDisplayString(cls.ContainingNamespace)
                        == TrecsNamespaces.Trecs
                )
                {
                    return true;
                }
            }
            return false;
        }

        static Location GetFieldLocation(IFieldSymbol field)
        {
            var refs = field.DeclaringSyntaxReferences;
            if (refs.Length > 0)
            {
                return refs[0].GetSyntax().GetLocation();
            }
            return Location.None;
        }

        static void Generate(SourceProductionContext context, RemovalHandlerModel model)
        {
            try
            {
                foreach (var diag in model.Diagnostics)
                {
                    var descriptor =
                        diag.Kind == DiagKindCascade
                            ? DiagnosticDescriptors.CascadeRemoveInvalidFieldType
                            : DiagnosticDescriptors.DisposeOnRemoveInvalidFieldType;

                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            descriptor,
                            diag.Location.ToLocation(),
                            diag.FieldName,
                            model.TypeName,
                            diag.FieldTypeDisplay
                        )
                    );
                }

                if (model.Fields.Length == 0)
                {
                    // Only invalid fields — diagnostics reported, nothing to emit.
                    return;
                }

                if (!model.IsPartial)
                {
                    // EntityComponentGenerator already reports TRECS132 for this;
                    // emitting a partial against a non-partial struct would just
                    // produce noisy follow-on errors, so skip codegen.
                    return;
                }

                var source = GenerateCode(model);
                context.AddSource(model.SafeFileName, source);
                SourceGenLogger.WriteGeneratedFile(model.SafeFileName, source);
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.UnhandledSourceGenError,
                        Location.None,
                        $"{model.TypeName}: {ex.Message}"
                    )
                );
            }
        }

        static string GenerateCode(RemovalHandlerModel model)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            CommonUsings.AppendTo(sb);
            sb.AppendLine();

            int indentLevel = 0;
            if (!string.IsNullOrEmpty(model.Namespace))
            {
                sb.AppendLine($"namespace {model.Namespace}");
                sb.AppendLine("{");
                indentLevel = 1;
            }

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
            string fullTypeName = $"{model.TypeName}{model.TypeParameterList}";
            sb.AppendLine(
                $"{structIndent}{model.Accessibility} partial struct {fullTypeName} : global::Trecs.IComponentRemovalHandlers"
            );
            sb.AppendLine($"{structIndent}{{");
            indentLevel++;

            var m = new string(' ', indentLevel * 4);

            bool anyCascade = model.Fields.Any(f => f.Cascade);
            bool anyDispose = model.Fields.Any(f => f.Dispose);

            sb.AppendLine($"{m}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{m}void global::Trecs.IComponentRemovalHandlers.RegisterRemovalHandlers(global::Trecs.RemovalHandlerCollector __collector)"
            );
            sb.AppendLine($"{m}{{");

            if (anyCascade)
            {
                EmitHandlerLambda(
                    sb,
                    indentLevel + 1,
                    fullTypeName,
                    registerCall: "AddCascadeReadHandler",
                    emitFieldBody: (b, bodyIndent) =>
                    {
                        foreach (var f in model.Fields)
                        {
                            if (!f.Cascade)
                                continue;
                            EmitCascadeFieldBody(b, bodyIndent, f);
                        }
                    }
                );
            }

            if (anyDispose)
            {
                EmitHandlerLambda(
                    sb,
                    indentLevel + 1,
                    fullTypeName,
                    registerCall: "AddDisposeHandler",
                    emitFieldBody: (b, bodyIndent) =>
                    {
                        foreach (var f in model.Fields)
                        {
                            if (!f.Dispose)
                                continue;
                            var fi = new string(' ', bodyIndent * 4);
                            b.AppendLine($"{fi}__c.{f.FieldName}.Dispose(__world);");
                        }
                    }
                );
            }

            sb.AppendLine($"{m}}}");

            while (indentLevel > 0)
            {
                indentLevel--;
                sb.AppendLine($"{new string(' ', indentLevel * 4)}}}");
            }

            return sb.ToString();
        }

        static void EmitHandlerLambda(
            StringBuilder sb,
            int indentLevel,
            string fullTypeName,
            string registerCall,
            Action<StringBuilder, int> emitFieldBody
        )
        {
            var i0 = new string(' ', indentLevel * 4);
            var i1 = new string(' ', (indentLevel + 1) * 4);
            var i2 = new string(' ', (indentLevel + 2) * 4);

            sb.AppendLine($"{i0}__collector.{registerCall}(static (__world, __group, __range) =>");
            sb.AppendLine($"{i0}{{");
            sb.AppendLine(
                $"{i1}var __buffer = __world.ComponentBuffer<{fullTypeName}>(__group).Read;"
            );
            sb.AppendLine($"{i1}for (int __i = __range.Start; __i < __range.End; __i++)");
            sb.AppendLine($"{i1}{{");
            sb.AppendLine($"{i2}ref readonly var __c = ref __buffer[__i];");
            emitFieldBody(sb, indentLevel + 2);
            sb.AppendLine($"{i1}}}");
            sb.AppendLine($"{i0}}});");
        }

        static void EmitCascadeFieldBody(StringBuilder sb, int indentLevel, RemovalFieldInfo f)
        {
            var ind = new string(' ', indentLevel * 4);
            if (f.CascadeShape == CascadeShapeSingleHandle)
            {
                sb.AppendLine(
                    $"{ind}if (__c.{f.FieldName}.Exists(__world)) {{ __c.{f.FieldName}.Remove(__world); }}"
                );
            }
            else if (f.CascadeShape == CascadeShapeHandleList)
            {
                sb.AppendLine($"{ind}foreach (var __h in __c.{f.FieldName}.Read(__world))");
                sb.AppendLine($"{ind}{{");
                sb.AppendLine($"{ind}    if (__h.Exists(__world)) {{ __h.Remove(__world); }}");
                sb.AppendLine($"{ind}}}");
            }
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

    internal readonly record struct RemovalFieldInfo(
        string FieldName,
        bool Cascade,
        bool Dispose,
        int CascadeShape
    );

    internal readonly record struct RemovalDiagnostic(
        int Kind,
        string FieldName,
        string FieldTypeDisplay,
        LocationInfo Location
    );

    internal readonly record struct RemovalHandlerModel(
        string TypeName,
        string TypeParameterList,
        string Namespace,
        string Accessibility,
        bool IsPartial,
        EquatableArray<RemovalFieldInfo> Fields,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        string SafeFileName,
        EquatableArray<RemovalDiagnostic> Diagnostics
    );
}
