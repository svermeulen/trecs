; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS038 | Trecs | Warning | Template generates many partitions
TRECS039 | Trecs | Error | Cannot register an abstract template
TRECS118 | Trecs | Error | [NonCopyable] struct must not be copied to a by-value local
TRECS120 | Trecs | Error | [NonCopyable] and [Copyable] cannot both be applied to the same struct
TRECS121 | Trecs | Error | [Input] component must not contain persistent-pointer fields
TRECS122 | Trecs | Error | [Input] component must not contain TrecsList fields
TRECS123 | Trecs | Error | [Input(MissingInputBehavior.Retain)] component cannot contain InputXxxPtr fields
TRECS124 | Trecs | Error | NativeSharedPtr<T> requires T to be a readonly struct
TRECS125 | Trecs | Error | SharedPtr<T> requires T (class or interface) to carry [Trecs.Immutable]
TRECS126 | Trecs | Error | [Immutable] type violates immutability rules
TRECS127 | Trecs | Warning | [Immutable] interface method returns a non-immutable type
TRECS128 | Trecs | Warning | Iteration over Dictionary/HashSet is non-deterministic
TRECS129 | Trecs | Warning | Iteration over NativeHashMap/NativeHashSet is non-deterministic
TRECS130 | Trecs | Warning | Non-deterministic API used in fixed-update system
TRECS131 | Trecs | Error | [NonCopyable] struct must not be passed by value
TRECS132 | Trecs | Error | IEntityComponent struct must be partial
TRECS133 | Trecs | Error | [NonCopyable] member invoked through a read-only reference makes a silent defensive copy
TRECS134 | Trecs | Error | [CascadeRemove] field must be EntityHandle or TrecsList<EntityHandle>
TRECS135 | Trecs | Error | [DisposeOnRemove] field must be a disposable Trecs heap type
TRECS136 | Trecs | Error | Persistent component must not contain input-pointer fields
TRECS137 | Trecs | Error | Component must not contain anchor fields

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS110 | Trecs | Error | NativeUniquePtr must not be copied to a by-value local — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
TRECS111 | Trecs | Error | NativeUniquePtr must not be passed by value — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
