#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Internal;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Source generator for all <c>ISystem</c> classes. Generates explicit interface
    /// implementation for <c>Ready</c> (with partial method hook), iteration-method
    /// wrappers, and the World/Shutdown wiring.
    ///
    /// <para>Pipeline shape: the transform produces a fully-precomputed
    /// <see cref="AutoSystemModel"/> (value-equatable, holds no symbols or syntax) and
    /// the terminal stage materializes diagnostics + emits source. Global-namespace
    /// name is folded in via a lightweight <see cref="IIncrementalGenerator"/> combine
    /// (a single string, value-equatable, doesn't pin the compilation).</para>
    /// </summary>
    [Generator]
    public class AutoSystemGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsSystemClass(s),
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null);
            var models = AssemblyFilterHelper.FilterByTrecsReference(modelsRaw, hasTrecsReference);

            // Only the global-namespace name leaves the CompilationProvider; the rest of
            // the compilation is not referenced in code generation. This stays value-
            // equatable so unrelated edits to other types do not invalidate the combine.
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var withGlobalNs = models.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                withGlobalNs,
                static (spc, source) => GenerateAutoSystemSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsSystemClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDecl
                && classDecl.BaseList != null
                && classDecl.BaseList.Types.Count > 0;
        }

        private static AutoSystemModel? BuildModel(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;

            var classSymbol =
                context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (classSymbol == null)
                return null;

            // Final gate: only types that implement Trecs.ISystem participate.
            bool isSystem = false;
            foreach (var i in classSymbol.AllInterfaces)
            {
                if (
                    SymbolAnalyzer.IsInNamespace(i.ContainingNamespace, "Trecs")
                    && i.Name == "ISystem"
                )
                {
                    isSystem = true;
                    break;
                }
            }
            if (!isSystem)
                return null;

            var diagnostics = new List<DiagnosticInfo>();
            AutoSystemInfo? info = null;
            bool isValid;
            try
            {
                isValid = ValidateAndCollect(
                    classDecl,
                    classSymbol,
                    diagnostics,
                    context.SemanticModel,
                    out info
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        classDecl.GetLocation(),
                        "AutoSystem validation",
                        ex.Message
                    )
                );
                isValid = false;
                info = null;
            }

            return new AutoSystemModel(
                ClassName: classDecl.Identifier.Text,
                Namespace: SymbolAnalyzer.GetNamespace(classDecl),
                HintFileName: SymbolAnalyzer.GetSafeFileName(classSymbol, "AutoSystem"),
                TypeParameterList: classDecl.TypeParameterList?.ToString() ?? string.Empty,
                ConstraintClauses: classDecl.ConstraintClauses.Count > 0
                    ? " " + string.Join(" ", classDecl.ConstraintClauses.Select(c => c.ToString()))
                    : string.Empty,
                IsValid: isValid,
                Info: info ?? AutoSystemInfo.Empty,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        private static void GenerateAutoSystemSource(
            SourceProductionContext context,
            AutoSystemModel model,
            string globalNamespaceName
        )
        {
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _timer_ = SourceGenTimer.Time("AutoSystemGenerator.Total");
                SourceGenLogger.Log($"[AutoSystemGenerator] Processing {model.ClassName}");

                var source = ErrorRecovery.TryExecute(
                    () => GenerateSourceCode(model, globalNamespaceName),
                    context,
                    Location.None,
                    "AutoSystem code generation"
                );

                if (source != null)
                {
                    context.AddSource(model.HintFileName, source);
                    SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            Location.None,
                            model.ClassName
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    Location.None,
                    $"AutoSystem {model.ClassName}",
                    ex
                );
            }
        }

        private static bool ValidateAndCollect(
            ClassDeclarationSyntax classDec,
            INamedTypeSymbol classSymbol,
            List<DiagnosticInfo> diagnostics,
            SemanticModel semanticModel,
            out AutoSystemInfo? autoSystemInfo
        )
        {
            autoSystemInfo = null;
            bool isValid = true;
            var className = classDec.Identifier.Text;

            // Check if the class is partial
            if (!classDec.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AutoSystemMustBePartial,
                        classDec.Identifier.GetLocation(),
                        className
                    )
                );
                isValid = false;
            }

            // Find all iteration methods in the class
            var hasWrapAsJobMethodNamedExecute = false;
            var iterationMethods = new List<IterationMethodInfo>();

            foreach (var methodDecl in classDec.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                if (methodSymbol == null)
                    continue;

                var iterationType = GetIterationType(methodSymbol);
                if (iterationType == null)
                    continue;

                // [WrapAsJob] methods are claimed by AutoJobGenerator, not by us.
                if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                {
                    if (methodDecl.Identifier.Text == "Execute")
                        hasWrapAsJobMethodNamedExecute = true;
                    continue;
                }

                var methodName = methodDecl.Identifier.Text;
                var methodLocation = LocationInfo.From(methodDecl.Identifier.GetLocation());

                if (methodSymbol.IsStatic)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.IterationMethodCannotBeStatic,
                            methodLocation,
                            methodName
                        )
                    );
                    isValid = false;
                }

                if (methodSymbol.IsAbstract)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.IterationMethodCannotBeAbstract,
                            methodLocation,
                            methodName
                        )
                    );
                    isValid = false;
                }

                var customParams = CollectCustomParams(
                    methodDecl,
                    semanticModel,
                    iterationType.Value
                );
                bool hasAnyAttributeCriteria = HasAnyAttributeCriteria(methodSymbol);

                // Custom params not allowed on methods named Execute
                if (customParams.Count > 0 && methodName == "Execute")
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.AutoSystemMethodHasCustomParams,
                            LocationInfo.From(methodDecl.Identifier.GetLocation()),
                            methodName
                        )
                    );
                    isValid = false;
                }

                iterationMethods.Add(
                    new IterationMethodInfo(
                        MethodName: methodName,
                        Type: iterationType.Value,
                        CustomParams: customParams.ToEquatableArray(),
                        HasAnyAttributeCriteria: hasAnyAttributeCriteria
                    )
                );
            }

            // Single pass over all method members for hook detection, Execute detection,
            // and conflict signature collection.
            var hasUserDefinedExecute = false;
            var userDefinedMethodSignatures = new List<string>();
            foreach (var member in classSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                userDefinedMethodSignatures.Add($"{member.Name}/{member.Parameters.Length}");

                if (member.Name == "Execute" && member.Parameters.Length == 0)
                    hasUserDefinedExecute = true;
            }

            // TRECS044: user-defined Execute() AND iteration method named Execute
            var hasIterationMethodNamedExecute = iterationMethods.Any(m =>
                m.MethodName == "Execute"
            );
            if (
                hasUserDefinedExecute
                && (hasIterationMethodNamedExecute || hasWrapAsJobMethodNamedExecute)
            )
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AutoSystemExecuteConflict,
                        LocationInfo.From(classDec.Identifier.GetLocation()),
                        className
                    )
                );
                isValid = false;
            }

            // TRECS047: system has iteration methods but no Execute entry point
            if (
                iterationMethods.Count > 0
                && !hasUserDefinedExecute
                && !hasIterationMethodNamedExecute
                && !hasWrapAsJobMethodNamedExecute
            )
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AutoSystemMissingExecute,
                        LocationInfo.From(classDec.Identifier.GetLocation()),
                        className
                    )
                );
                isValid = false;
            }

            if (isValid)
            {
                autoSystemInfo = new AutoSystemInfo(
                    IterationMethods: iterationMethods.ToEquatableArray(),
                    HasUserDefinedExecute: hasUserDefinedExecute,
                    HasIterationMethodNamedExecute: hasIterationMethodNamedExecute,
                    UserDefinedMethodSignatures: userDefinedMethodSignatures.ToEquatableArray()
                );
            }

            return isValid;
        }

        /// <summary>
        /// Walks the method's parameter list and collects only those that need to be
        /// forwarded by the auto-system wrapper as user-visible parameters — i.e. those
        /// explicitly marked <c>[PassThroughArgument]</c>. Loop-managed and component
        /// parameters are bound by the iteration generators themselves; the wrapper just
        /// forwards customs.
        /// </summary>
        private static List<CustomParamInfo> CollectCustomParams(
            MethodDeclarationSyntax methodDecl,
            SemanticModel semanticModel,
            IterationType iterationType
        )
        {
            var customParams = new List<CustomParamInfo>();

            if (iterationType == IterationType.EntityFilterComponents)
            {
                bool readingComponents = true;
                foreach (var param in methodDecl.ParameterList.Parameters)
                {
                    var paramType =
                        param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                    if (paramType == null)
                        continue;

                    if (SymbolAnalyzer.IsLoopManagedType(paramType))
                    {
                        readingComponents = false;
                        continue;
                    }

                    bool isComponent = paramType.AllInterfaces.Any(i =>
                        i.Name == "IEntityComponent"
                    );
                    if (readingComponents && !isComponent)
                        readingComponents = false;
                    if (readingComponents && isComponent)
                        continue;

                    AddCustomParamIfMarked(param, paramType, semanticModel, customParams);
                }
            }
            else if (iterationType == IterationType.EntityFilterAspect)
            {
                var parameters = methodDecl.ParameterList.Parameters;
                // Skip the aspect (index 0) and loop-managed types; collect only explicit
                // [PassThroughArgument] params as custom.
                for (int i = 1; i < parameters.Count; i++)
                {
                    var param = parameters[i];
                    var paramType =
                        param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                    if (paramType == null)
                        continue;
                    if (SymbolAnalyzer.IsLoopManagedType(paramType))
                        continue;
                    AddCustomParamIfMarked(param, paramType, semanticModel, customParams);
                }
            }
            else if (iterationType == IterationType.RunOnce)
            {
                // RunOnce methods consume [SingleEntity] params via RunOnceGenerator's
                // (WorldAccessor) overload. The auto-system wrapper just forwards
                // [PassThroughArgument] params (typically none for Execute, since Execute
                // can't have customs by design).
                foreach (var param in methodDecl.ParameterList.Parameters)
                {
                    var paramType =
                        param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                    if (paramType == null)
                        continue;
                    AddCustomParamIfMarked(param, paramType, semanticModel, customParams);
                }
            }

            return customParams;
        }

        private static void AddCustomParamIfMarked(
            ParameterSyntax param,
            ITypeSymbol paramType,
            SemanticModel semanticModel,
            List<CustomParamInfo> customParams
        )
        {
            var paramSymbol = semanticModel.GetDeclaredSymbol(param);
            if (paramSymbol == null)
                return;
            if (
                !PerformanceCache.HasAttributeByName(
                    paramSymbol,
                    TrecsAttributeNames.PassThroughArgument,
                    TrecsNamespaces.Trecs
                )
            )
                return;

            var paramIsRef = param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
            var paramIsIn = param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));

            customParams.Add(
                new CustomParamInfo(
                    TypeName: PerformanceCache.GetDisplayString(paramType),
                    TypeNamespace: PerformanceCache.GetDisplayString(paramType.ContainingNamespace),
                    ParamName: param.Identifier.ToString(),
                    IsRef: paramIsRef,
                    IsIn: paramIsIn
                )
            );
        }

        // IterationType distinguishes the three code paths AutoSystemGenerator's emission
        // switches on: [ForEachEntity] aspect/components iteration, and RunOnce (methods
        // with [SingleEntity] parameters and no [ForEachEntity] / [WrapAsJob]). The
        // aspect-vs-components split for [ForEachEntity] is determined by the method's
        // parameter shape.
        private static IterationType? GetIterationType(IMethodSymbol methodSymbol)
        {
            if (IterationAttributeRouting.HasEntityFilter(methodSymbol))
            {
                return IterationAttributeRouting.RoutesToAspectGenerator(methodSymbol)
                    ? IterationType.EntityFilterAspect
                    : IterationType.EntityFilterComponents;
            }
            if (IterationAttributeRouting.IsRunOnceMethod(methodSymbol))
            {
                return IterationType.RunOnce;
            }
            return null;
        }

        /// <summary>
        /// Returns true when the iteration attribute has at least one criterion property
        /// (Tags/Tag/Set/MatchByComponents). Used to decide whether to generate the
        /// auto-wrapper that calls Method(__world): without any criteria there is no
        /// (WorldAccessor) overload to call.
        /// <para>
        /// RunOnce methods always have criteria (the per-parameter <c>[SingleEntity]</c>
        /// inline tags), so we shortcut to <c>true</c> for them.
        /// </para>
        /// </summary>
        private static bool HasAnyAttributeCriteria(IMethodSymbol methodSymbol)
        {
            if (IterationAttributeRouting.IsRunOnceMethod(methodSymbol))
                return true;

            foreach (var attr in PerformanceCache.GetAttributes(methodSymbol))
            {
                var name = attr.AttributeClass?.Name;
                if (name != TrecsAttributeNames.ForEachEntity)
                    continue;

                // C# 11 generic-attribute form: [ForEachEntity<A>] etc. Roslyn's Name
                // strips arity, so the same name-check matches generic and non-generic.
                if (
                    attr.AttributeClass is INamedTypeSymbol namedClass
                    && namedClass.TypeArguments.Length > 0
                )
                    return true;

                // Positional ctor: [ForEachEntity(typeof(A))] / [ForEachEntity(typeof(A), typeof(B))].
                foreach (var ctorArg in attr.ConstructorArguments)
                {
                    if (
                        ctorArg.Kind == TypedConstantKind.Type
                        || ctorArg.Kind == TypedConstantKind.Array
                    )
                        return true;
                }

                foreach (var namedArg in attr.NamedArguments)
                {
                    switch (namedArg.Key)
                    {
                        case "Tag":
                        case "Tags":
                        case "Set":
                        case "MatchByComponents":
                            return true;
                    }
                }
            }
            return false;
        }

        private static HashSet<string> GetRequiredNamespaces(
            string globalNamespaceName,
            AutoSystemInfo autoSystemInfo
        )
        {
            var namespaces = new HashSet<string>(CommonUsings.Namespaces) { "System" };

            foreach (var method in autoSystemInfo.IterationMethods)
            {
                foreach (var param in method.CustomParams)
                {
                    var ns = param.TypeNamespace;
                    if (
                        !string.IsNullOrEmpty(ns)
                        && ns != "System"
                        && !ns.StartsWith("System.")
                        && ns != globalNamespaceName
                    )
                    {
                        namespaces.Add(ns);
                    }
                }
            }

            return namespaces;
        }

        private static string GenerateSourceCode(AutoSystemModel model, string globalNamespaceName)
        {
            var sb = OptimizedStringBuilder.ForAspect(0);

            var requiredNamespaces = GetRequiredNamespaces(globalNamespaceName, model.Info);
            sb.AppendUsings(requiredNamespaces.ToArray());

            return sb.WrapInNamespace(
                    model.Namespace,
                    (builder) =>
                    {
                        builder.AppendLine(
                            1,
                            $"partial class {model.ClassName}{model.TypeParameterList} : Trecs.Internal.ISystemInternal{model.ConstraintClauses}"
                        );
                        builder.AppendLine(1, "{");

                        GenerateInitialize(builder);
                        GenerateWrappers(builder, model.Info);

                        // GenerateExecute intentionally removed — systems must define their own Execute

                        builder.AppendLine(1, "}");
                    }
                )
                .ToString();
        }

        private static void GenerateInitialize(OptimizedStringBuilder sb)
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, "WorldAccessor __world;");
            sb.AppendLine();
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, "public WorldAccessor World => __world;");
            sb.AppendLine();
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, "WorldAccessor Trecs.Internal.ISystemInternal.World");
            sb.AppendLine(2, "{");
            sb.AppendLine(3, "get => __world;");
            sb.AppendLine(
                3,
                "set { TrecsDebugAssert.That(__world == null, \"World has already been set\"); __world = value; }"
            );
            sb.AppendLine(2, "}");
            sb.AppendLine();
            sb.AppendLine(2, "partial void OnReady();");
            sb.AppendLine();
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, "void Trecs.Internal.ISystemInternal.Ready()");
            sb.AppendLine(2, "{");
            sb.AppendLine(3, "OnReady();");
            sb.AppendLine(2, "}");
            sb.AppendLine();
            sb.AppendLine(2, "partial void OnShutdown();");
            sb.AppendLine();
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, "void Trecs.Internal.ISystemInternal.Shutdown()");
            sb.AppendLine(2, "{");
            sb.AppendLine(3, "OnShutdown();");
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateWrappers(OptimizedStringBuilder sb, AutoSystemInfo info)
        {
            // Rebuild the signature lookup as a HashSet for O(1) Contains checks during the
            // wrapper-skip decision. The model carries it as a flat EquatableArray so the
            // upstream comparison is structural; the HashSet is a terminal-stage convenience.
            var signatureSet = new HashSet<string>(info.UserDefinedMethodSignatures);

            foreach (var method in info.IterationMethods)
            {
                var wrapperParamCount = method.HasCustomParams ? method.CustomParams.Length : 0;
                var wrapperKey = $"{method.MethodName}/{wrapperParamCount}";
                if (signatureSet.Contains(wrapperKey))
                    continue;

                // No criteria on the iteration attribute → no (WorldAccessor) convenience
                // overload was generated, so the auto-wrapper would not compile. The user
                // must call the method explicitly with a builder, e.g.
                // `Method(World.Query().WithTags<X>())` from their own Execute().
                if (!method.HasAnyAttributeCriteria)
                    continue;

                var visibility = method.MethodName == "Execute" ? "public " : "";

                if (method.HasCustomParams)
                {
                    sb.AppendLine(2, GeneratedCodeAttributes.Line);
                    sb.AppendLine(
                        2,
                        $"{visibility}void {method.MethodName}({method.CustomParamsDeclaration})"
                    );
                    sb.AppendLine(2, "{");
                    sb.AppendLine(3, $"{method.MethodName}(__world, {method.CustomParamsCall});");
                    sb.AppendLine(2, "}");
                }
                else
                {
                    sb.AppendLine(2, GeneratedCodeAttributes.Line);
                    sb.AppendLine(2, $"{visibility}void {method.MethodName}()");
                    sb.AppendLine(2, "{");
                    sb.AppendLine(3, $"{method.MethodName}(__world);");
                    sb.AppendLine(2, "}");
                }
                sb.AppendLine();
            }
        }
    }

    /// <summary>
    /// Pipeline-boundary model for an auto-system class. Equatable through and through —
    /// strings, bools, and <see cref="EquatableArray{T}"/> only. Symbol-walking happens
    /// in <c>BuildModel</c>; nothing past that point sees Roslyn types.
    /// </summary>
    internal sealed record AutoSystemModel(
        string ClassName,
        string Namespace,
        string HintFileName,
        string TypeParameterList,
        string ConstraintClauses,
        bool IsValid,
        AutoSystemInfo Info,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    internal enum IterationType
    {
        EntityFilterComponents,
        EntityFilterAspect,

        /// <summary>
        /// Method has one or more <c>[SingleEntity]</c> parameters and no other
        /// iteration attribute. <see cref="RunOnceGenerator"/> emits the
        /// <c>(WorldAccessor)</c> overload that resolves each singleton then calls
        /// the user method exactly once.
        /// </summary>
        RunOnce,
    }

    internal sealed record IterationMethodInfo(
        string MethodName,
        IterationType Type,
        EquatableArray<CustomParamInfo> CustomParams,
        bool HasAnyAttributeCriteria
    )
    {
        public bool HasCustomParams => CustomParams.Length > 0;

        public string CustomParamsDeclaration =>
            string.Join(
                ", ",
                CustomParams.Select(p =>
                    $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.TypeName} {p.ParamName}"
                )
            );

        public string CustomParamsCall =>
            string.Join(
                ", ",
                CustomParams.Select(p => $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.ParamName}")
            );
    }

    internal readonly record struct CustomParamInfo(
        string TypeName,
        string TypeNamespace,
        string ParamName,
        bool IsRef,
        bool IsIn
    );

    internal sealed record AutoSystemInfo(
        EquatableArray<IterationMethodInfo> IterationMethods,
        bool HasUserDefinedExecute,
        bool HasIterationMethodNamedExecute,
        EquatableArray<string> UserDefinedMethodSignatures
    )
    {
        public static readonly AutoSystemInfo Empty = new(
            EquatableArray<IterationMethodInfo>.Empty,
            HasUserDefinedExecute: false,
            HasIterationMethodNamedExecute: false,
            UserDefinedMethodSignatures: EquatableArray<string>.Empty
        );
    }
}
