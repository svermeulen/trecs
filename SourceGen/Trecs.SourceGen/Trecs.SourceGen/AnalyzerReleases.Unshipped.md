; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS001 | Trecs | Error | Invalid parameter modifiers
TRECS002 | Trecs | Error | No parameters defined
TRECS003 | Trecs | Error | Invalid return type
TRECS004 | Trecs | Error | Class must be partial
TRECS005 | Trecs | Error | Invalid parameter list
TRECS006 | Trecs | Error | Missing execute method on EntityJobGroup
TRECS007 | Trecs | Error | Multiple execute methods on EntityJobGroup
TRECS008 | Trecs | Error | Invalid job parameter list
TRECS009 | Trecs | Error | Aspect must be partial
TRECS010 | Trecs | Error | Aspect must be a struct
TRECS011 | Trecs | Error | Single value component must be a struct
TRECS012 | Trecs | Error | Single value component must implement IEntityComponent
TRECS013 | Trecs | Error | Single value component must have exactly one field
TRECS014 | Trecs | Error | Aspect must specify Tags or MatchByComponents
TRECS015 | Trecs | Error | Could not resolve symbol
TRECS016 | Trecs | Error | Duplicate component type
TRECS020 | Trecs | Error | Aspect interface must be partial
TRECS022 | Trecs | Error | Aspect parameter must use 'in' modifier, not 'ref'
TRECS023 | Trecs | Error | Aspect has no component types
TRECS025 | Trecs | Error | Duplicate loop parameter
TRECS026 | Trecs | Error | Cannot mix aspect parameter with direct component parameters
TRECS027 | Trecs | Error | Parameter must use 'in' modifier
TRECS028 | Trecs | Error | Parameter must be passed by value
TRECS029 | Trecs | Error | Component parameter must use 'in' or 'ref'
TRECS030 | Trecs | Error | Template must be partial
TRECS031 | Trecs | Error | Template must be a class
TRECS032 | Trecs | Error | Invalid attribute combination on template component field
TRECS033 | Trecs | Error | Template field must implement IEntityComponent
TRECS034 | Trecs | Error | Template field must be public
TRECS036 | Trecs | Error | Component contains managed (reference type) fields
TRECS037 | Trecs | Error | Globals template field must have an explicit default
TRECS040 | Trecs | Error | AutoSystem class must be partial
TRECS043 | Trecs | Error | Iteration method named Execute must not have custom parameters
TRECS044 | Trecs | Error | AutoSystem Execute conflict
TRECS045 | Trecs | Error | System already defines World property
TRECS046 | Trecs | Error | System already defines _world field
TRECS047 | Trecs | Error | System has no Execute method
TRECS050 | Trecs | Error | Iteration method cannot be static
TRECS051 | Trecs | Error | Iteration method cannot be abstract
TRECS052 | Trecs | Error | Method has multiple iteration attributes
TRECS053 | Trecs | Error | Both Tag and Tags specified
TRECS060 | Trecs | Error | Old-style DeclareDependencies hook detected
TRECS061 | Trecs | Error | Old-style Ready hook detected
TRECS062 | Trecs | Error | Partial method uses old hook name
TRECS070 | Trecs | Warning | Use the generated ScheduleParallel/Schedule member instead of Unity's raw schedule extension
TRECS071 | Trecs | Error | Unsupported [FromWorld] field type
TRECS073 | Trecs | Error | Job nested inside generic outer type
TRECS074 | Trecs | Error | Job struct must be partial
TRECS075 | Trecs | Error | [FromWorld] does not support multi-variable field declarations
TRECS076 | Trecs | Error | Custom non-iteration job missing Execute method
TRECS077 | Trecs | Error | Custom non-iteration job's Execute must be parameterless
TRECS078 | Trecs | Error | Custom job's Execute method must be public
TRECS079 | Trecs | Error | Parallel job [FromWorld] write field missing [NativeDisableParallelForRestriction]
TRECS081 | Trecs | Error | Trecs container field missing [FromWorld]
TRECS082 | Trecs | Error | [FromWorld] inline tags not supported for EntityIndex fields
TRECS083 | Trecs | Error | [FromWorld] too many inline tags
TRECS084 | Trecs | Error | [FromWorld] inline tags not supported for NativeWorldAccessor
TRECS090 | Trecs | Error | [WrapAsJob] method must be static
TRECS091 | Trecs | Error | [WrapAsJob] method cannot take WorldAccessor
TRECS092 | Trecs | Error | [WrapAsJob] cannot be used on struct methods
TRECS093 | Trecs | Error | [WrapAsJob] pass-through parameter must be unmanaged
TRECS094 | Trecs | Error | [WrapAsJob] pass-through parameter cannot be ref/out
TRECS095 | Trecs | Error | [WrapAsJob] does not support set-based filtering
TRECS096 | Trecs | Error | [WrapAsJob] requires query criteria
TRECS097 | Trecs | Error | [WrapAsJob] Execute cannot have [PassThroughArgument] parameters
TRECS098 | Trecs | Error | SetAccessor/SetRead/SetWrite cannot be used in [WrapAsJob] methods
TRECS099 | Trecs | Error | NativeSetRead/NativeSetWrite cannot be used in main-thread [ForEachEntity] methods
TRECS100 | Trecs | Error | Unrecognized parameter type requires [PassThroughArgument]
TRECS101 | Trecs | Error | Unsupported [FromWorld] parameter type on [WrapAsJob] method
TRECS102 | Trecs | Error | [FromWorld] on [WrapAsJob] requires inline Tag/Tags
TRECS110 | Trecs | Error | NativeUniquePtr must not be copied to a by-value local
TRECS111 | Trecs | Error | NativeUniquePtr must not be passed by value
TRECS996 | Trecs | Error | Source generation error
TRECS999 | Trecs | Error | Source generation error
