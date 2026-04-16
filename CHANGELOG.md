# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-03-27

### Added

- Core ECS framework: World, WorldBuilder, WorldAccessor, EntityQuerier
- Source-generated systems with `[ForEachEntity]` and `[ForEachAspect]` attributes
- Entity templates via `ITemplate`, `ITags<>`, and typed field declarations
- Deterministic fixed-update loop with separate RNG streams
- Component interpolation system for smooth rendering
- Burst/Jobs integration with `IJobSystem` and native entity operations
- Entity handles with versioned references (`EntityHandle`) and transient indices (`EntityIndex`)
- Entity groups based on tag composition
- Sets for dynamic entity collections with deferred add/remove
- Query API with fluent `QueryBuilder` supporting tag, component, and set filtering
- Input queuing system for network/replay scenarios
- ECS state serialization support
- Custom high-performance collections (DenseDictionary, FastList, NativeBag)
- Memory management with SharedPtr, UniquePtr, NativeSharedPtr, NativeUniquePtr
- Frame-scoped allocation variants for transient data
- Blob caching for large data structures
- Unity Test Framework test suite
- 12 sample projects demonstrating progressive complexity
