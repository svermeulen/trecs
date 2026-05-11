using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen
{
    internal static class DiagnosticDescriptors
    {
        public const string TrecsCategory = "Trecs";

        // ForEach diagnostics (TRECS001-008; TRECS006 deleted as duplicate).
        // NOTE: descriptor declarations below are not strictly sorted by ID; the
        // file order has not been re-shuffled to preserve git blame.

        public static readonly DiagnosticDescriptor InvalidParameterModifiers = new(
            id: "TRECS001",
            title: "Invalid parameter modifiers",
            messageFormat: "Parameter '{0}' must be marked with the 'in' modifier",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor EmptyParameters = new(
            id: "TRECS002",
            title: "No parameters defined",
            messageFormat: "[ForEachEntity] iteration method must have at least one per-entity parameter (a component, aspect, or EntityIndex)",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor InvalidReturnType = new(
            id: "TRECS003",
            title: "Invalid return type",
            messageFormat: "[ForEachEntity] iteration method must return void",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor NotPartialClass = new(
            id: "TRECS004",
            title: "Class must be partial",
            messageFormat: "Class '{0}' must be marked as partial since it contains a source-generated [ForEachEntity] iteration method, [SingleEntity] parameters, or a [WrapAsJob] method",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor InvalidParameterList = new(
            id: "TRECS005",
            title: "Invalid parameter list",
            messageFormat: "Invalid [ForEachEntity] parameter list: {0}. Parameters may appear in any order. Component parameters must use 'in' or 'ref'. Use [PassThroughArgument] on a parameter whose type is IEntityComponent / EntityIndex / WorldAccessor when you want it forwarded as a user-supplied value rather than auto-detected.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor DuplicateLoopParameter = new(
            id: "TRECS025",
            title: "Duplicate loop parameter",
            messageFormat: "Parameter '{0}' duplicates an earlier loop-supplied {1} parameter. Use [PassThroughArgument] on this parameter to forward it as a user-supplied value instead.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor MixedAspectAndComponentParams = new(
            id: "TRECS026",
            title: "Cannot mix aspect parameter with direct component parameters",
            messageFormat: "Iteration method '{0}' has both an aspect parameter and a direct component parameter '{1}'. This is not supported — aspects are the canonical way to declare a method's component requirements in Trecs. Add component '{2}' to the aspect's IRead<T> / IWrite<T> interface list and access it through the aspect view instead. Aspects are typically per-method, so this is usually a one-line addition.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor EntityJobGroupMultipleExecuteMethods = new(
            id: "TRECS007",
            title: "Multiple execute methods on EntityJobGroup",
            messageFormat: "Entity group job '{0}' has multiple Execute methods. Only one is allowed.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor InvalidJobParameterList = new(
            id: "TRECS008",
            title: "Invalid job parameter list",
            messageFormat: "{0}",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // Aspect diagnostics (TRECS009, 012, 013, 015, 016, 020, 022, 023; TRECS010/011/014/017/018/019/021 retired)

        public static readonly DiagnosticDescriptor AspectMustBePartial = new(
            id: "TRECS009",
            title: "Aspect must be partial",
            messageFormat: "Aspect '{0}' must be marked as partial since it implements IAspect",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor UnwrapComponentMustImplementIEntityComponent =
            new(
                id: "TRECS012",
                title: "Single value component must implement IEntityComponent",
                messageFormat: "Single value component '{0}' must implement IEntityComponent",
                category: TrecsCategory,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );

        public static readonly DiagnosticDescriptor UnwrapComponentMustHaveExactlyOneField = new(
            id: "TRECS013",
            title: "Single value component must have exactly one field",
            messageFormat: "Single value component '{0}' must have exactly one field, but has {1}",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor CouldNotResolveSymbol = new(
            id: "TRECS015",
            title: "Could not resolve symbol",
            messageFormat: "Could not resolve symbol '{0}'",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor DuplicateComponentType = new(
            id: "TRECS016",
            title: "Duplicate component type",
            messageFormat: "Duplicate component type '{0}' found",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // TRECS017 and TRECS018 removed - duplicates of TRECS012 and TRECS013

        // TRECS019 and TRECS021 are retired. TRECS019 (AspectInterfaceNotFound) diagnosed a
        // reference to a non-aspect-interface in a struct aspect's base list — no longer
        // reachable: the new detection filters aspect interfaces by IAspect membership
        // upstream. TRECS021 (CircularAspectInterfaceReference) diagnosed cycles in the
        // aspect-interface hierarchy — C# itself rejects these with CS0529 before the
        // generator runs.

        public static readonly DiagnosticDescriptor AspectInterfaceMustBePartial = new(
            id: "TRECS020",
            title: "Aspect interface must be partial",
            messageFormat: "Aspect interface '{0}' must be marked as partial — the source generator attaches a partial interface declaring the ref-returning property contract for each IRead<>/IWrite<> component",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );
        public static readonly DiagnosticDescriptor AspectParamMustBeIn = new(
            id: "TRECS022",
            title: "Aspect parameter must use 'in' modifier, not 'ref'",
            messageFormat: "Parameter '{0}' must use the 'in' modifier, not 'ref'. Aspect parameters should always be passed as 'in'.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor AspectNoComponents = new(
            id: "TRECS023",
            title: "Aspect has no component types",
            messageFormat: "Aspect '{0}' must declare at least one component type via IRead<> or IWrite<> interfaces",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor ParameterMustBeIn = new(
            id: "TRECS027",
            title: "Parameter must use 'in' modifier",
            messageFormat: "Parameter '{0}' of type '{1}' must be passed with the 'in' modifier",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor ParameterMustBeByValue = new(
            id: "TRECS028",
            title: "Parameter must be passed by value",
            messageFormat: "Parameter '{0}' must be passed by value (no 'in', 'ref', or 'out' modifier)",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor ComponentParameterMustBeInOrRef = new(
            id: "TRECS029",
            title: "Component parameter must use 'in' or 'ref'",
            messageFormat: "Component parameter '{0}' must use 'in' (read-only) or 'ref' (read-write)",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // Template diagnostics (TRECS030-038; 039 unused)

        public static readonly DiagnosticDescriptor TemplateMustBePartial = new(
            id: "TRECS030",
            title: "Template must be partial",
            messageFormat: "Template '{0}' must be marked as partial since it implements ITemplate",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TemplateMustBeClass = new(
            id: "TRECS031",
            title: "Template must be a class",
            messageFormat: "Template '{0}' must be a class, not a struct",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TemplateFieldMustHaveNoAccessModifier = new(
            id: "TRECS034",
            title: "Template field must not have an access modifier",
            messageFormat: "Field '{0}' in template '{1}' must not declare an access modifier (omit 'public', 'private', etc.) — template fields are config, not API",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TemplateInvalidAttributeCombination = new(
            id: "TRECS032",
            title: "Invalid attribute combination on template component field",
            messageFormat: "Field '{0}' in template '{1}' has conflicting attributes [{2}] and [{3}]",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TemplateFieldMustBeEntityComponent = new(
            id: "TRECS033",
            title: "Template field must implement IEntityComponent",
            messageFormat: "Field '{0}' of type '{1}' in template '{2}' must implement IEntityComponent",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor GlobalsTemplateFieldMustHaveDefault = new(
            id: "TRECS037",
            title: "Globals template field must have an explicit default",
            messageFormat: "Field '{0}' in globals template '{1}' must have an explicit default value (e.g. '= default;'). The global entity is created automatically by the system, so there is no opportunity to provide values via an initializer.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor ComponentHasManagedFields = new(
            id: "TRECS036",
            title: "Component contains managed (reference type) fields",
            messageFormat: "Component '{0}' (field '{1}' in template '{2}') contains managed field '{3}' of type '{4}'. Components must be unmanaged (no reference types) for NativeArray/Burst compatibility.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor VariableUpdateOnlyOnInvalidTarget = new(
            id: "TRECS035",
            title: "[VariableUpdateOnly] applied to a non-template class",
            messageFormat: "[VariableUpdateOnly] on type '{0}' is silently ignored — the attribute is only meaningful on a class implementing ITemplate or on a template's component field. Remove it or apply it to one of those targets.",
            category: TrecsCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TemplatePartitionCountHigh = new(
            id: "TRECS038",
            title: "Template generates many partitions",
            messageFormat: "Template '{0}' generates {1} partitions across {2} dimensions ({3}). Each partition is a distinct group with its own per-component buffer. Past ~16 partitions consider sets — they don't multiply. See docs/guides/entity-subset-patterns.md.",
            category: TrecsCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        // AutoSystem diagnostics (TRECS040, 043, 044, 047)

        public static readonly DiagnosticDescriptor AutoSystemMustBePartial = new(
            id: "TRECS040",
            title: "AutoSystem class must be partial",
            messageFormat: "System class '{0}' must be marked as partial to support source generation",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor AutoSystemMethodHasCustomParams = new(
            id: "TRECS043",
            title: "Iteration method named Execute must not have custom parameters",
            messageFormat: "Method '{0}' is named 'Execute' and cannot have custom parameters since its wrapper is the ISystem entry point. Rename the method or remove custom parameters.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor AutoSystemExecuteConflict = new(
            id: "TRECS044",
            title: "AutoSystem Execute conflict",
            messageFormat: "Class '{0}' has both a user-defined Execute() and an iteration method named 'Execute'. Rename the iteration method to avoid ambiguity.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor AutoSystemMissingExecute = new(
            id: "TRECS047",
            title: "System has no Execute method",
            messageFormat: "System class '{0}' has iteration methods but no Execute entry point. Either name one iteration method 'Execute' or define an explicit 'public void Execute()' method.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // Iteration method diagnostics (TRECS050, 051, 053)

        public static readonly DiagnosticDescriptor IterationMethodCannotBeStatic = new(
            id: "TRECS050",
            title: "Iteration method cannot be static",
            messageFormat: "Method '{0}' cannot be static when marked with [ForEachEntity] or when it carries [SingleEntity] parameters",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor IterationMethodCannotBeAbstract = new(
            id: "TRECS051",
            title: "Iteration method cannot be abstract",
            messageFormat: "Method '{0}' cannot be abstract when marked with [ForEachEntity] or when it carries [SingleEntity] parameters",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor TagAndTagsBothSpecified = new(
            id: "TRECS053",
            title: "Both Tag and Tags specified",
            messageFormat: "'{0}' specifies both Tag and Tags on [{1}]. Use one or the other.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // Job scheduling diagnostics (TRECS070, 071, 073-079; 072 unused)

        public static readonly DiagnosticDescriptor JobInsideGenericOuterTypeNotSupported = new(
            id: "TRECS073",
            title: "Job nested inside generic outer type",
            messageFormat: "Job '{0}' is nested inside the generic type '{1}' — JobGenerator cannot redeclare the outer type's type parameters and constraints. Move the job out of the generic outer type, or factor it into a non-generic helper.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor JobMustBePartial = new(
            id: "TRECS074",
            title: "Job struct must be partial",
            messageFormat: "Job '{0}' has [FromWorld] or [ForEachEntity] markers but is not declared as 'partial'. Add the 'partial' keyword so the source generator can emit ScheduleParallel/Schedule methods.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor CustomJobMissingExecuteMethod = new(
            id: "TRECS076",
            title: "Custom non-iteration job missing Execute method",
            messageFormat: "Custom job '{0}' has [FromWorld] fields but no parameterless 'void Execute()' method. Add 'void Execute() {{ ... }}' so the generator can wrap it as an IJob.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor CustomJobExecuteMustBeParameterless = new(
            id: "TRECS077",
            title: "Custom non-iteration job's Execute must be parameterless",
            messageFormat: "Custom job '{0}'.Execute must be parameterless. Add an [ForEachEntity] attribute on Execute if you want per-entity iteration; otherwise use plain fields (or [FromWorld]) for inputs.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor CustomJobExecuteMustBePublic = new(
            id: "TRECS078",
            title: "Custom job's Execute method must be public",
            messageFormat: "Custom job '{0}'.Execute must be declared 'public' so it directly satisfies the IJob/IJobFor interface (the generator no longer emits a visibility shim).",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor MultiVariableFromWorldFieldNotSupported = new(
            id: "TRECS075",
            title: "[FromWorld] does not support multi-variable field declarations",
            messageFormat: "Field declaration '{0}' declares more than one variable with [FromWorld]. Split into separate field declarations so each can be wired up unambiguously.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor UnsupportedFromWorldFieldType = new(
            id: "TRECS071",
            title: "Unsupported [FromWorld] field type",
            messageFormat: "Field type '{0}' is not supported by [FromWorld]. Expected one of: NativeComponentBufferRead<T>, NativeComponentBufferWrite<T>, NativeComponentRead<T>, NativeComponentWrite<T>, NativeComponentLookupRead<T>, NativeComponentLookupWrite<T>, NativeSetCommandBuffer<TSet>, NativeSetRead<TSet>, NativeEntitySetIndices<TSet>, or Aspect.NativeFactory.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor RawScheduleWithTrecsFields = new(
            id: "TRECS070",
            title: "Use the generated ScheduleParallel/Schedule member instead of Unity's raw schedule extension",
            messageFormat: "Job '{0}' has Trecs typed fields ({1}) — use the generated ScheduleParallel(WorldAccessor, ...) overload (iteration job) or Schedule(WorldAccessor, ...) overload (custom job) instead of Unity's '{2}' extension to ensure proper dependency tracking",
            category: TrecsCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor MissingNativeDisableParallelForRestriction =
            new(
                id: "TRECS079",
                title: "Parallel job [FromWorld] write field missing [NativeDisableParallelForRestriction]",
                messageFormat: "Parallel job '{0}' has [FromWorld] write field '{1}' (type '{2}') without [Unity.Collections.LowLevel.Unsafe.NativeDisableParallelForRestriction]. Add the attribute — parallel writes to disjoint indices are safe under Trecs's per-(component, group) dep tracking, but Unity's job walker requires the opt-in.",
                category: TrecsCategory,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );

        // [FromWorld] field validation diagnostics (TRECS081-084; 080/085-089 unused)

        public static readonly DiagnosticDescriptor MissingFromWorldOnContainerField = new(
            id: "TRECS081",
            title: "Trecs container field missing [FromWorld]",
            messageFormat: "Field '{0}' of type '{1}' on Trecs job '{2}' must have [FromWorld] for dependency tracking. Without it, the scheduler cannot detect that this job accesses this data, which can lead to race conditions with other jobs.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor FromWorldInlineTagsNotSupportedForEntityIndex =
            new(
                id: "TRECS082",
                title: "[FromWorld] inline tags not supported for EntityIndex fields",
                messageFormat: "[FromWorld] field '{0}' is a NativeComponent type that requires an EntityIndex, not a TagSet. Remove Tag/Tags from [FromWorld] and supply the EntityIndex at schedule time.",
                category: TrecsCategory,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );

        public static readonly DiagnosticDescriptor FromWorldTooManyInlineTags = new(
            id: "TRECS083",
            title: "[FromWorld] too many inline tags",
            messageFormat: "[FromWorld] field '{0}' specifies {1} tags, but TagSet supports at most 4. Split the tags across multiple fields or use a runtime TagSet parameter instead.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor FromWorldInlineTagsNotSupportedForNativeWorldAccessor =
            new(
                id: "TRECS084",
                title: "[FromWorld] inline tags not supported for NativeWorldAccessor",
                messageFormat: "[FromWorld] field '{0}' is a NativeWorldAccessor which has no group resolution. Remove Tag/Tags from [FromWorld]; NativeWorldAccessor is always constructed via world.ToNative().",
                category: TrecsCategory,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );

        // AutoJob / [WrapAsJob] diagnostics (TRECS090, 091, 093, 094, 096-100; 092/095 unused)

        public static readonly DiagnosticDescriptor WrapAsJobNonStatic = new(
            id: "TRECS090",
            title: "[WrapAsJob] method must be static",
            messageFormat: "[WrapAsJob] method '{0}' must be static. Static methods cannot access instance state, which ensures job-compatibility. Compiler errors for invalid access will appear in your code, not in generated code.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor WrapAsJobWorldAccessorParam = new(
            id: "TRECS091",
            title: "[WrapAsJob] method cannot take WorldAccessor",
            messageFormat: "[WrapAsJob] method '{0}' has a WorldAccessor parameter. Jobs cannot use WorldAccessor — use NativeWorldAccessor for structural operations.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor WrapAsJobManagedPassThrough = new(
            id: "TRECS093",
            title: "[WrapAsJob] pass-through parameter must be unmanaged",
            messageFormat: "[WrapAsJob] method '{0}' has [PassThroughArgument] parameter '{1}' of managed type '{2}'. Job fields must be unmanaged.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor WrapAsJobRefPassThrough = new(
            id: "TRECS094",
            title: "[WrapAsJob] pass-through parameter cannot be ref/out",
            messageFormat: "[WrapAsJob] method '{0}' has [PassThroughArgument] parameter '{1}' with ref/out modifier. Job fields are value copies and cannot be passed by reference.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor SetAccessorNotAllowedInJob = new(
            id: "TRECS098",
            title: "SetAccessor/SetRead/SetWrite cannot be used in [WrapAsJob] methods",
            messageFormat: "Parameter '{0}' uses a main-thread-only set type for set type '{1}'. Use NativeSetRead<{1}> or NativeSetCommandBuffer<{1}> for job-compatible set access.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor NativeSetNotAllowedOnMainThread = new(
            id: "TRECS099",
            title: "NativeSetRead/NativeSetCommandBuffer cannot be used in main-thread [ForEachEntity] methods",
            messageFormat: "Parameter '{0}' uses {1} which is job-only. Use SetAccessor<{2}>, SetRead<{2}>, or SetWrite<{2}> for main-thread set access.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor WrapAsJobEmptyCriteria = new(
            id: "TRECS096",
            title: "[WrapAsJob] requires query criteria",
            messageFormat: "[WrapAsJob] method '{0}' has [ForEachEntity] with no Tags or MatchByComponents. The generated job needs query criteria to know which entities to iterate. Add Tags or MatchByComponents to [ForEachEntity].",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor WrapAsJobExecutePassThrough = new(
            id: "TRECS097",
            title: "[WrapAsJob] Execute cannot have [PassThroughArgument] parameters",
            messageFormat: "[WrapAsJob] method '{0}' is named 'Execute' on an ISystem class and will serve as the ISystem entry point. [PassThroughArgument] parameters would change the wrapper signature. Remove them or rename the method.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // General diagnostics (TRECS99x)

        public static readonly DiagnosticDescriptor SourceGenerationError = new(
            id: "TRECS996",
            title: "Source generation error",
            messageFormat: "An error occurred during {0}: {1}",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor UnrecognizedParameterType = new(
            id: "TRECS100",
            title: "Unrecognized parameter type requires [PassThroughArgument]",
            messageFormat: "Parameter '{0}' has unrecognized type '{1}'. Mark it [PassThroughArgument] to forward as a user-supplied value, or use a recognized type (component with in/ref, EntityIndex, WorldAccessor, SetAccessor<T>).",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // [FromWorld] on [WrapAsJob] diagnostics (TRECS101-102)

        public static readonly DiagnosticDescriptor FromWorldUnsupportedOnWrapAsJob = new(
            id: "TRECS101",
            title: "Unsupported [FromWorld] parameter type on [WrapAsJob] method",
            messageFormat: "[FromWorld] parameter '{0}' of type '{1}' is not supported on [WrapAsJob] methods. "
                + "For NativeWorldAccessor, NativeSetRead/Write use the first-class parameter support instead. "
                + "For NativeComponentRead/Write use [PassThroughArgument]. "
                + "Supported [FromWorld] types: Aspect.NativeFactory, NativeComponentLookupRead<T>, NativeComponentLookupWrite<T>, "
                + "NativeComponentBufferRead<T>, NativeComponentBufferWrite<T>, NativeEntitySetIndices<T>, GroupIndex, NativeEntityHandleBuffer.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor FromWorldRequiresInlineTagsOnWrapAsJob = new(
            id: "TRECS102",
            title: "[FromWorld] on [WrapAsJob] requires inline Tag/Tags",
            messageFormat: "[FromWorld] parameter '{0}' on [WrapAsJob] method requires inline Tag or Tags "
                + "(e.g. [FromWorld(typeof(MyTag))]). The generated wrapper method has no way "
                + "to accept runtime TagSets. Use a manual job struct if runtime-variable tags are needed.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // ── NativeUniquePtr copy prevention (TRECS110-111) ────────────────────────

        public static readonly DiagnosticDescriptor NativeUniquePtrByValueLocal = new(
            id: "TRECS110",
            title: "NativeUniquePtr must not be copied to a by-value local",
            messageFormat: "NativeUniquePtr<{0}> must not be copied to a local variable; "
                + "access the owning field directly to preserve write-access tracking",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor NativeUniquePtrByValueParameter = new(
            id: "TRECS111",
            title: "NativeUniquePtr must not be passed by value",
            messageFormat: "Parameter '{0}' of type NativeUniquePtr<{1}> must be declared as ref, in, or out — "
                + "not by value — to preserve write-access tracking",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // ── per-parameter / per-field [SingleEntity] (TRECS112-116) ────────

        public static readonly DiagnosticDescriptor SingleEntityWrongType = new(
            id: "TRECS112",
            title: "[SingleEntity] parameter or field must be an aspect or component",
            messageFormat: "[SingleEntity] target '{0}' has type '{1}' which is neither an IAspect nor an IEntityComponent. "
                + "[SingleEntity] resolves to a single matched entity, so the type must be a Trecs aspect "
                + "(implementing IAspect) or a component (implementing IEntityComponent).",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor SingleEntityWrongModifier = new(
            id: "TRECS113",
            title: "[SingleEntity] parameter has wrong modifier",
            messageFormat: "[SingleEntity] parameter '{0}' must be declared 'in' for an aspect or read-only component, "
                + "or 'ref' for a writable component. Bare-by-value and 'out' are not supported.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor SingleEntityRequiresInlineTags = new(
            id: "TRECS114",
            title: "[SingleEntity] requires inline Tag or Tags",
            messageFormat: "[SingleEntity] target '{0}' must specify an inline Tag or Tags "
                + "(e.g. [SingleEntity(typeof(MyTag))]). [SingleEntity] has no runtime override "
                + "— the singleton is resolved at the same site every call. "
                + "If you need a runtime-supplied query, use "
                + "World.Query().WithTags(...).Single() directly inside the method body.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor SingleEntityConflictingAttributes = new(
            id: "TRECS115",
            title: "[SingleEntity] conflicts with [FromWorld] or [PassThroughArgument]",
            messageFormat: "[SingleEntity] on '{0}' cannot be combined with [{1}]. [SingleEntity] alone is the carrier "
                + "for world-sourced single-entity values (it implies [FromWorld] semantics).",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor SingleEntityWriteAspectMissingNativeDisableParallelForRestriction =
            new(
                id: "TRECS116",
                title: "[SingleEntity] write-aspect field on a parallel job needs [NativeDisableParallelForRestriction]",
                messageFormat: "Field '{0}' on parallel job '{1}' has [SingleEntity] with aspect '{2}' that contains IWrite components. "
                    + "Add [NativeDisableParallelForRestriction] to the field — Unity's parallel-job safety walker rejects the materialized aspect's NativeComponentBufferWrite without it.",
                category: TrecsCategory,
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true
            );

        public static readonly DiagnosticDescriptor GlobalIndexParamMustBeInt = new(
            id: "TRECS117",
            title: "[GlobalIndex] parameter must be int",
            messageFormat: "Parameter '{0}' on '{1}' is marked [GlobalIndex] but its type is '{2}'. The packed query index is always an int — change the parameter type to int.",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static readonly DiagnosticDescriptor UnhandledSourceGenError = new(
            id: "TRECS999",
            title: "Source generation error",
            messageFormat: "An error occurred during source generation: {0}",
            category: TrecsCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );
    }
}
