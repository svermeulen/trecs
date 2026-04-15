# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-03-27

### Added

- Core ECS framework: World, WorldBuilder, EcsAccessor, EntityQueryer
- Source-generated systems with `[ForEachEntity]` and `[ForEachAspect]` attributes
- Entity templates via `ITemplate`, `ITags<>`, and typed field declarations
- Deterministic fixed-update loop with separate RNG streams
- Component interpolation system for smooth rendering
- Burst/Jobs integration with `IJobSystem` and native entity operations
- Permission-based component access with `EcsAccessorBuilder`
- Entity filters and filter collections
- Entity references with versioned handles (`EntityRef`)
- Entity groups based on tag composition
- Input queuing system for network/replay scenarios
- ECS state serialization support
- Custom high-performance collections (DenseDictionary, FastList)
- Memory management with SharedPtr, UniquePtr, NativeSharedPtr
- Blob caching for large data structures
- Unity Test Framework test suite
- Sample project (HelloEntity)
