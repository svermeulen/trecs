## Build

**Main project (Unity):**
Open `UnityProject/Trecs/Trecs.sln` in Unity 6000.3.10f1+ or your IDE.

**Source generator:**
```bash
dotnet build SourceGen/Trecs.SourceGen/Trecs.SourceGen.sln -c Release
```

To rebuild and install the source generator DLL into the Unity project:
```bash
cd SourceGen/Trecs.SourceGen && ./build_and_install.sh
```

## Tests

Run tests via Unity Test Runner (Window > General > Test Runner) in **EditMode**.

## Code Style

Uses [CSharpier](https://csharpier.com/) for formatting:
```bash
cd UnityProject/Trecs && dotnet tool restore && dotnet csharpier .
```

## Project Structure

- `UnityProject/Trecs/Assets/com.trecs.core/` - Core ECS library
- `UnityProject/Trecs/Assets/Trecs.Tests/` - Unit tests (editor-only)
- `UnityProject/Trecs/Assets/Samples/` - Sample projects
- `SourceGen/Trecs.SourceGen/` - Roslyn source generator

## Conventions

- Components are unmanaged structs implementing `IEntityComponent`
- Tags are marker structs implementing `ITag`
- Entity templates implement `ITemplate` with `ITags<>` and field declarations
- Systems implement `ISystem` or `IJobSystem`
- Namespaces follow folder structure under `Trecs` / `Trecs.Internal`
