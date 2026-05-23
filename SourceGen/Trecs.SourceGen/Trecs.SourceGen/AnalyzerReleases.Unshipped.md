; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS038 | Trecs | Warning | Template generates many partitions
TRECS039 | Trecs | Error | Cannot register an abstract template
TRECS118 | Trecs | Error | [NonCopyable] struct must not be copied to a by-value local
TRECS119 | Trecs | Error | [NonCopyable] struct must not be passed by value
TRECS120 | Trecs | Error | [NonCopyable] and [Copyable] cannot both be applied to the same struct
TRECS121 | Trecs | Error | [Input] component must not contain persistent-pointer fields
TRECS122 | Trecs | Error | [Input] component must not contain TrecsList fields
TRECS123 | Trecs | Error | [Input(MissingInputBehavior.Retain)] component cannot contain InputXxxPtr fields
TRECS124 | Trecs | Error | NativeSharedPtr<T> requires T to be a readonly struct
TRECS125 | Trecs | Error | SharedPtr<T> requires T (class or interface) to carry [Trecs.Immutable]
TRECS126 | Trecs | Error | [Immutable] type violates immutability rules
TRECS127 | Trecs | Warning | [Immutable] interface method returns a non-immutable type

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS110 | Trecs | Error | NativeUniquePtr must not be copied to a by-value local — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
TRECS111 | Trecs | Error | NativeUniquePtr must not be passed by value — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
