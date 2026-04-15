# Contributing to Trecs

Thank you for your interest in contributing to Trecs! This document provides guidelines and instructions for contributing.

## Reporting Bugs

Please use the [bug report template](https://github.com/svermeulen/trecs/issues/new?template=bug_report.yml) when filing issues. Include:

- Your Unity version
- Your Trecs version
- Steps to reproduce the issue
- Expected vs actual behavior
- Any relevant logs or screenshots

## Suggesting Features

Use the [feature request template](https://github.com/svermeulen/trecs/issues/new?template=feature_request.yml). Describe the use case and why the feature would be valuable.

## Development Setup

### Prerequisites

- **Unity** 6000.3.10f1 or later
- **.NET SDK** 6.0+ (for the source generator)
- **Git**

### Getting Started

1. Fork and clone the repository:
   ```bash
   git clone https://github.com/<your-username>/trecs.git
   ```

2. Open the Unity project:
   - Open Unity Hub
   - Add the project at `UnityProject/Trecs/`
   - Open with Unity 6000.3.10f1+

3. The core library is at `Assets/com.trecs.core/` and tests are at `Assets/Trecs.Tests/`.

### Building the Source Generator

The Roslyn source generator is a separate .NET project:

```bash
cd SourceGen/Trecs.SourceGen
dotnet build -c Release
```

To install the built DLL into the Unity project:

```bash
cd SourceGen/Trecs.SourceGen
./build_and_install.sh
```

### Running Tests

Tests use the Unity Test Framework and run in **EditMode**:

1. Open Unity
2. Go to **Window > General > Test Runner**
3. Select **EditMode**
4. Click **Run All**

### Code Style

This project uses [CSharpier](https://csharpier.com/) for code formatting. To format your code:

```bash
cd UnityProject/Trecs
dotnet tool restore
dotnet csharpier .
```

To check formatting without modifying files:

```bash
dotnet csharpier --check .
```

## Pull Request Process

1. Create a feature branch from `main`
2. Make your changes
3. Ensure all tests pass
4. Run the code formatter (`dotnet csharpier .`)
5. Submit a pull request with a clear description of the changes

### PR Guidelines

- Keep PRs focused on a single change
- Include tests for new functionality
- Update documentation if you change public APIs
- Follow existing code conventions

## License

By contributing to Trecs, you agree that your contributions will be licensed under the [MIT License](LICENSE).
