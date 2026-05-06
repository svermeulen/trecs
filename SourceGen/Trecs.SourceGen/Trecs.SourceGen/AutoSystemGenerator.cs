#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Internal;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Source generator for all ISystem classes. Generates
    /// explicit interface implementation for Ready (with partial method hook),
    /// iteration method wrappers, and Execute.
    /// </summary>
    [Generator]
    public class AutoSystemGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var classProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsSystemClass(s),
                    transform: static (ctx, _) => GetClassData(ctx)
                )
                .Where(static m => m is not null);
            var classProvider = AssemblyFilterHelper.FilterByTrecsReference(
                classProviderRaw,
                hasTrecsReference
            );

            // See IncrementalForEachGenerator for the caching rationale: validation runs
            // in the transform, and the terminal stage only needs the global-namespace
            // display string.
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var classWithGlobalNs = classProvider.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                classWithGlobalNs,
                static (spc, source) => GenerateAutoSystemSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsSystemClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDecl
                && classDecl.BaseList != null
                && classDecl.BaseList.Types.Count > 0;
        }

        private static AutoSystemClassData? GetClassData(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;

            var classSymbol =
                context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (classSymbol == null)
                return null;

            bool isSystem = false;
            foreach (var i in classSymbol.AllInterfaces)
            {
                if (SymbolAnalyzer.IsInNamespace(i.ContainingNamespace, "Trecs"))
                {
                    if (i.Name == "ISystem")
                    {
                        isSystem = true;
                    }
                }
            }

            if (!isSystem)
                return null;

            // Validate in the transform so the terminal stage doesn't need the full
            // Compilation. Diagnostics are replayed downstream. Unexpected exceptions
            // surface as a SourceGenerationError diagnostic rather than a generator crash.
            var diagnostics = new List<Diagnostic>();
            AutoSystemInfo? autoSystemInfo = null;
            bool isValid;
            try
            {
                isValid = ValidateAndCollect(
                    classDecl,
                    classSymbol,
                    diagnostics.Add,
                    context.SemanticModel,
                    out autoSystemInfo
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        classDecl.GetLocation(),
                        "AutoSystem validation",
                        ex.Message
                    )
                );
                isValid = false;
                autoSystemInfo = null;
            }

            return new AutoSystemClassData(
                classDecl,
                classSymbol,
                isValid,
                autoSystemInfo,
                diagnostics.ToImmutableArray()
            );
        }

        private static void GenerateAutoSystemSource(
            SourceProductionContext context,
            AutoSystemClassData data,
            string globalNamespaceName
        )
        {
            var location = data.ClassDecl.GetLocation();
            var className = data.ClassDecl.Identifier.Text;
            var fileName = SymbolAnalyzer.GetSafeFileName(data.ClassSymbol, "AutoSystem");

            // Replay diagnostics collected in the transform phase.
            foreach (var diag in data.Diagnostics)
            {
                context.ReportDiagnostic(diag);
            }

            if (!data.IsValid || data.AutoSystemInfo == null)
            {
                return;
            }

            try
            {
                using var _timer_ = SourceGenTimer.Time("AutoSystemGenerator.Total");
                SourceGenLogger.Log($"[AutoSystemGenerator] Processing {className}");

                var source = ErrorRecovery.TryExecute(
                    () =>
                        GenerateSourceCode(
                            data.ClassDecl,
                            data.AutoSystemInfo,
                            globalNamespaceName
                        ),
                    context,
                    location,
                    "AutoSystem code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            location,
                            className
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, $"AutoSystem {className}", ex);
            }
        }

        private static bool ValidateAndCollect(
            ClassDeclarationSyntax classDec,
            INamedTypeSymbol classSymbol,
            System.Action<Diagnostic> reportDiagnostic,
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
                reportDiagnostic(
                    Diagnostic.Create(
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
                var methodLocation = methodDecl.Identifier.GetLocation();

                // Validate iteration method modifiers
                if (methodSymbol.IsStatic)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.IterationMethodCannotBeStatic,
                            methodLocation,
                            methodName
                        )
                    );
                    isValid = false;
                }

                if (methodSymbol.IsAbstract)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.IterationMethodCannotBeAbstract,
                            methodLocation,
                            methodName
                        )
                    );
                    isValid = false;
                }

                var customParams = new List<CustomParamInfo>();
                bool hasAnyAttributeCriteria = HasAnyAttributeCriteria(methodSymbol);

                if (iterationType == IterationType.EntityFilterComponents)
                {
                    bool readingComponents = true;
                    var methodReadTypes = new List<ITypeSymbol>();
                    var methodWriteTypes = new List<ITypeSymbol>();

                    foreach (var param in methodDecl.ParameterList.Parameters)
                    {
                        var paramType =
                            param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                        if (paramType == null)
                            continue;

                        // Skip loop-managed types (WorldAccessor, EntityIndex, SetAccessor<T>)
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
                        {
                            var isRef = param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
                            var isIn = param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));

                            if (isRef)
                                methodWriteTypes.Add(paramType);
                            else if (isIn)
                                methodReadTypes.Add(paramType);
                        }
                        else if (!readingComponents)
                        {
                            // Only include params explicitly marked [PassThroughArgument]
                            var paramSymbol = semanticModel.GetDeclaredSymbol(param);
                            if (
                                paramSymbol != null
                                && PerformanceCache.HasAttributeByName(
                                    paramSymbol,
                                    TrecsAttributeNames.PassThroughArgument,
                                    TrecsNamespaces.Trecs
                                )
                            )
                            {
                                var paramIsRef = param.Modifiers.Any(m =>
                                    m.IsKind(SyntaxKind.RefKeyword)
                                );
                                var paramIsIn = param.Modifiers.Any(m =>
                                    m.IsKind(SyntaxKind.InKeyword)
                                );
                                customParams.Add(
                                    new CustomParamInfo(
                                        PerformanceCache.GetDisplayString(paramType),
                                        paramType,
                                        param.Identifier.ToString(),
                                        paramIsRef,
                                        paramIsIn
                                    )
                                );
                            }
                        }
                    }
                }
                else if (iterationType == IterationType.EntityFilterAspect)
                {
                    var parameters = methodDecl.ParameterList.Parameters;
                    if (parameters.Count > 0)
                    {
                        var firstParam = parameters[0];
                        var firstParamType =
                            firstParam.Type != null
                                ? semanticModel.GetTypeInfo(firstParam.Type).Type
                                : null;

                        // Collect only explicit [PassThroughArgument] params as custom,
                        // skipping the aspect (index 0) and loop-managed types.
                        for (int i = 1; i < parameters.Count; i++)
                        {
                            var param = parameters[i];
                            var paramType =
                                param.Type != null
                                    ? semanticModel.GetTypeInfo(param.Type).Type
                                    : null;
                            if (paramType == null)
                                continue;

                            // Skip loop-managed types
                            if (SymbolAnalyzer.IsLoopManagedType(paramType))
                                continue;

                            // Only include params explicitly marked [PassThroughArgument]
                            var paramSymbol = semanticModel.GetDeclaredSymbol(param);
                            if (
                                paramSymbol != null
                                && PerformanceCache.HasAttributeByName(
                                    paramSymbol,
                                    TrecsAttributeNames.PassThroughArgument,
                                    TrecsNamespaces.Trecs
                                )
                            )
                            {
                                var paramIsRef = param.Modifiers.Any(m =>
                                    m.IsKind(SyntaxKind.RefKeyword)
                                );
                                var paramIsIn = param.Modifiers.Any(m =>
                                    m.IsKind(SyntaxKind.InKeyword)
                                );
                                customParams.Add(
                                    new CustomParamInfo(
                                        PerformanceCache.GetDisplayString(paramType),
                                        paramType,
                                        param.Identifier.ToString(),
                                        paramIsRef,
                                        paramIsIn
                                    )
                                );
                            }
                        }
                    }
                }

                else if (iterationType == IterationType.RunOnce)
                {
                    // RunOnce methods consume [SingleEntity] params via RunOnceGenerator's
                    // generated (WorldAccessor) overload. The auto-system wrapper just
                    // forwards [PassThroughArgument] params (typically none for Execute,
                    // since Execute can't have customs by design).
                    foreach (var param in methodDecl.ParameterList.Parameters)
                    {
                        var paramSymbol = semanticModel.GetDeclaredSymbol(param);
                        if (paramSymbol == null)
                            continue;
                        if (
                            !PerformanceCache.HasAttributeByName(
                                paramSymbol,
                                TrecsAttributeNames.PassThroughArgument,
                                TrecsNamespaces.Trecs
                            )
                        )
                            continue;
                        var paramType =
                            param.Type != null
                                ? semanticModel.GetTypeInfo(param.Type).Type
                                : null;
                        if (paramType == null)
                            continue;
                        var paramIsRef = param.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.RefKeyword)
                        );
                        var paramIsIn = param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));
                        customParams.Add(
                            new CustomParamInfo(
                                PerformanceCache.GetDisplayString(paramType),
                                paramType,
                                param.Identifier.ToString(),
                                paramIsRef,
                                paramIsIn
                            )
                        );
                    }
                }

                // Custom params not allowed on methods named Execute
                if (customParams.Count > 0 && methodName == "Execute")
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.AutoSystemMethodHasCustomParams,
                            methodDecl.Identifier.GetLocation(),
                            methodName
                        )
                    );
                    isValid = false;
                }

                iterationMethods.Add(
                    new IterationMethodInfo(
                        methodName,
                        iterationType.Value,
                        customParams,
                        hasAnyAttributeCriteria
                    )
                );
            }

            // Single pass over all method members for hook detection, Execute detection,
            // and conflict signature collection
            var hasUserDefinedExecute = false;
            var userDefinedMethodSignatures = new HashSet<string>();
            foreach (var member in classSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                userDefinedMethodSignatures.Add($"{member.Name}/{member.Parameters.Length}");

                // Detect user-defined Execute
                if (member.Name == "Execute" && member.Parameters.Length == 0)
                {
                    hasUserDefinedExecute = true;
                }

                if (member.ExplicitInterfaceImplementations.Length > 0)
                    continue;

                var location = member.Locations.FirstOrDefault() ?? Location.None;

                // Old-style Ready() — not partial
                if (
                    member.Name == "Ready"
                    && member.Parameters.Length == 0
                    && !member.IsPartialDefinition
                    && member.PartialDefinitionPart == null
                )
                {
                    reportDiagnostic(
                        Diagnostic.Create(DiagnosticDescriptors.OldStyleInitializeHook, location)
                    );
                    isValid = false;
                }

                // Partial method with wrong name (user wrote "partial void Ready()" instead of "partial void OnReady()")
                if (
                    member.Name == "Ready"
                    && member.Parameters.Length == 0
                    && (member.IsPartialDefinition || member.PartialDefinitionPart != null)
                )
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.PartialMethodWrongName,
                            location,
                            "Ready",
                            "OnReady"
                        )
                    );
                    isValid = false;
                }

                if (
                    member.Name == "DeclareDependencies"
                    && member.Parameters.Length == 1
                    && member.Parameters[0].Type.Name == "IAccessDeclarations"
                    && (member.IsPartialDefinition || member.PartialDefinitionPart != null)
                )
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.PartialMethodWrongName,
                            location,
                            "DeclareDependencies",
                            "OnDeclareDependencies"
                        )
                    );
                    isValid = false;
                }
            }

            // Check for TRECS044: user-defined Execute() AND iteration method named Execute
            var hasIterationMethodNamedExecute = iterationMethods.Any(m =>
                m.MethodName == "Execute"
            );
            if (
                hasUserDefinedExecute
                && (hasIterationMethodNamedExecute || hasWrapAsJobMethodNamedExecute)
            )
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.AutoSystemExecuteConflict,
                        classDec.Identifier.GetLocation(),
                        className
                    )
                );
                isValid = false;
            }

            // Check for TRECS047: system has iteration methods but no Execute entry point
            if (
                iterationMethods.Count > 0
                && !hasUserDefinedExecute
                && !hasIterationMethodNamedExecute
                && !hasWrapAsJobMethodNamedExecute
            )
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.AutoSystemMissingExecute,
                        classDec.Identifier.GetLocation(),
                        className
                    )
                );
                isValid = false;
            }

            if (isValid)
            {
                autoSystemInfo = new AutoSystemInfo(
                    iterationMethods,
                    hasUserDefinedExecute,
                    hasIterationMethodNamedExecute,
                    userDefinedMethodSignatures
                );
            }

            return isValid;
        }

        // IterationType distinguishes the three code paths AutoSystemGenerator's
        // emission switches on: [ForEachEntity] aspect/components iteration, and
        // RunOnce (methods with [SingleEntity] parameters and no [ForEachEntity] /
        // [WrapAsJob]). The aspect-vs-components split for [ForEachEntity] is
        // determined by the method's parameter shape.
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
        /// auto-wrapper that calls Method(_world): without any criteria there is no
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
                if (name != TrecsAttributeNames.EntityFilter)
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

            void AddNamespaceIfNeeded(ITypeSymbol typeSymbol)
            {
                var ns = PerformanceCache.GetDisplayString(typeSymbol.ContainingNamespace);
                if (
                    !string.IsNullOrEmpty(ns)
                    && ns != "System"
                    && ns != null
                    && !ns.StartsWith("System.")
                    && ns != globalNamespaceName
                )
                {
                    namespaces.Add(ns);
                }
            }

            foreach (var method in autoSystemInfo.IterationMethods)
            {
                foreach (var param in method.CustomParams)
                {
                    if (param.TypeSymbol != null)
                        AddNamespaceIfNeeded(param.TypeSymbol);
                }
            }

            return namespaces;
        }

        private static string GenerateSourceCode(
            ClassDeclarationSyntax classDec,
            AutoSystemInfo autoSystemInfo,
            string globalNamespaceName
        )
        {
            var namespaceName = SymbolAnalyzer.GetNamespace(classDec);
            var className = classDec.Identifier.Text;
            var typeParams = classDec.TypeParameterList?.ToString() ?? "";
            var constraints =
                classDec.ConstraintClauses.Count > 0
                    ? " " + string.Join(" ", classDec.ConstraintClauses.Select(c => c.ToString()))
                    : "";

            var sb = OptimizedStringBuilder.ForAspect(0);

            var requiredNamespaces = GetRequiredNamespaces(globalNamespaceName, autoSystemInfo);
            sb.AppendUsings(requiredNamespaces.ToArray());

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        builder.AppendLine(
                            1,
                            $"partial class {className}{typeParams} : Trecs.Internal.ISystemInternal{constraints}"
                        );
                        builder.AppendLine(1, "{");

                        // Generate partial method + explicit interface impl for Initialize
                        GenerateInitialize(builder);

                        // DeclareDependencies removed — runtime job scheduler handles deps implicitly

                        // Generate iteration method wrappers
                        GenerateWrappers(builder, autoSystemInfo);

                        // GenerateExecute intentionally removed — systems must define their own Execute

                        builder.AppendLine(1, "}");
                    }
                )
                .ToString();
        }

        private static void GenerateInitialize(OptimizedStringBuilder sb)
        {
            sb.AppendLine(2, "WorldAccessor _world;");
            sb.AppendLine();
            sb.AppendLine(2, "public WorldAccessor World => _world;");
            sb.AppendLine();
            sb.AppendLine(2, "WorldAccessor Trecs.Internal.ISystemInternal.World");
            sb.AppendLine(2, "{");
            sb.AppendLine(3, "get => _world;");
            sb.AppendLine(
                3,
                "set { Assert.That(_world == null, \"World has already been set\"); _world = value; }"
            );
            sb.AppendLine(2, "}");
            sb.AppendLine();
            sb.AppendLine(2, "partial void OnReady();");
            sb.AppendLine();
            sb.AppendLine(2, "void Trecs.Internal.ISystemInternal.Ready()");
            sb.AppendLine(2, "{");
            sb.AppendLine(3, "OnReady();");
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateWrappers(
            OptimizedStringBuilder sb,
            AutoSystemInfo autoSystemInfo
        )
        {
            foreach (var method in autoSystemInfo.IterationMethods)
            {
                // Skip if user already defined a method with the wrapper's signature
                var wrapperParamCount = method.HasCustomParams ? method.CustomParams.Count : 0;
                var wrapperKey = $"{method.MethodName}/{wrapperParamCount}";
                if (autoSystemInfo.UserDefinedMethodSignatures.Contains(wrapperKey))
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
                    sb.AppendLine(
                        2,
                        $"{visibility}void {method.MethodName}({method.CustomParamsDeclaration})"
                    );
                    sb.AppendLine(2, "{");
                    sb.AppendLine(3, $"{method.MethodName}(_world, {method.CustomParamsCall});");
                    sb.AppendLine(2, "}");
                }
                else
                {
                    sb.AppendLine(2, $"{visibility}void {method.MethodName}()");
                    sb.AppendLine(2, "{");
                    sb.AppendLine(3, $"{method.MethodName}(_world);");
                    sb.AppendLine(2, "}");
                }
                sb.AppendLine();
            }
        }
    }

    internal class AutoSystemClassData
    {
        public ClassDeclarationSyntax ClassDecl { get; }
        public INamedTypeSymbol ClassSymbol { get; }
        public bool IsValid { get; }
        public AutoSystemInfo? AutoSystemInfo { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public AutoSystemClassData(
            ClassDeclarationSyntax classDecl,
            INamedTypeSymbol classSymbol,
            bool isValid,
            AutoSystemInfo? autoSystemInfo,
            ImmutableArray<Diagnostic> diagnostics
        )
        {
            ClassDecl = classDecl;
            ClassSymbol = classSymbol;
            IsValid = isValid;
            AutoSystemInfo = autoSystemInfo;
            Diagnostics = diagnostics;
        }
    }

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

    internal class IterationMethodInfo
    {
        public string MethodName { get; }
        public IterationType Type { get; }
        public List<CustomParamInfo> CustomParams { get; }
        public bool HasCustomParams => CustomParams.Count > 0;

        /// <summary>
        /// True when the iteration attribute supplied at least one criterion (Tags/Tag/Set/
        /// Sets/MatchByComponents). When false, the source generator does not emit a
        /// (WorldAccessor) convenience overload, so AutoSystemGenerator must skip its
        /// auto-wrapper or the generated code will reference a non-existent method.
        /// </summary>
        public bool HasAnyAttributeCriteria { get; }

        public IterationMethodInfo(
            string methodName,
            IterationType type,
            List<CustomParamInfo> customParams,
            bool hasAnyAttributeCriteria
        )
        {
            MethodName = methodName;
            Type = type;
            CustomParams = customParams;
            HasAnyAttributeCriteria = hasAnyAttributeCriteria;
        }

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

    internal class CustomParamInfo
    {
        public string TypeName { get; }
        public ITypeSymbol? TypeSymbol { get; }
        public string ParamName { get; }
        public bool IsRef { get; }
        public bool IsIn { get; }

        public CustomParamInfo(
            string typeName,
            ITypeSymbol? typeSymbol,
            string paramName,
            bool isRef,
            bool isIn
        )
        {
            TypeName = typeName;
            TypeSymbol = typeSymbol;
            ParamName = paramName;
            IsRef = isRef;
            IsIn = isIn;
        }
    }

    internal class AutoSystemInfo
    {
        public List<IterationMethodInfo> IterationMethods { get; }
        public bool HasUserDefinedExecute { get; }
        public bool HasIterationMethodNamedExecute { get; }
        public HashSet<string> UserDefinedMethodSignatures { get; }

        public AutoSystemInfo(
            List<IterationMethodInfo> iterationMethods,
            bool hasUserDefinedExecute,
            bool hasIterationMethodNamedExecute,
            HashSet<string> userDefinedMethodSignatures
        )
        {
            IterationMethods = iterationMethods;
            HasUserDefinedExecute = hasUserDefinedExecute;
            HasIterationMethodNamedExecute = hasIterationMethodNamedExecute;
            UserDefinedMethodSignatures = userDefinedMethodSignatures;
        }
    }
}
