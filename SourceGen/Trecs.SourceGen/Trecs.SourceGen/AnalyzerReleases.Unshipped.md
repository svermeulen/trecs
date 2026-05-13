; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS038 | Trecs | Warning | Template generates many partitions
TRECS039 | Trecs | Error | Cannot register an abstract template

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TRECS110 | Trecs | Error | NativeUniquePtr must not be copied to a by-value local — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
TRECS111 | Trecs | Error | NativeUniquePtr must not be passed by value — replaced by per-allocation AtomicSafetyHandle on NativeUniqueRead/Write wrappers
