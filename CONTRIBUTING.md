# Contributing to GameShift

Thank you for your interest in contributing to GameShift! This guide will help you get set up and submit your first pull request.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) 17.12+ (or VS Code with C# Dev Kit)
- Windows 10 version 2004+ (x64)
- Git

## Development Setup

```bash
# Clone the repository
git clone https://github.com/lhceist41/GameShift.git
cd GameShift

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run unit tests
dotnet test

# Run the application
dotnet run --project src/GameShift.App
```

## Project Structure

```
src/
  GameShift.App/         # WPF application — views, viewmodels, services, UI plumbing
  GameShift.Core/        # Core logic — detection, optimization, profiles, monitoring
  GameShift.Tests/       # Unit tests (xUnit + Moq)
```

- **GameShift.App** is the UI layer. It depends on GameShift.Core but never the reverse.
- **GameShift.Core** contains all business logic with zero UI dependencies.
- **GameShift.Tests** covers Core logic. Tests must pass before any PR is merged.

## Coding Standards

- Follow the existing code style — the project uses an `.editorconfig` for formatting
- Use `PascalCase` for public members, `_camelCase` for private fields
- Prefer `async/await` for I/O-bound operations
- Every optimization module implements the `IOptimization` interface with `ApplyAsync` and `RevertAsync`
- Keep GameShift.Core free of WPF or UI references

## Adding a New Optimization

1. Create a new class in `src/GameShift.Core/Optimization/` implementing `IOptimization`
2. Implement `ApplyAsync` (apply the optimization) and `RevertAsync` (restore original state)
3. Use `SystemStateSnapshot` to record original values before making changes
4. Register the module in `OptimizationEngine`
5. Add unit tests in `GameShift.Tests`

## Pull Request Process

1. **Fork** the repository
2. **Create a feature branch** from `master` (`git checkout -b feature/your-feature`)
3. **Make your changes** — keep commits focused and descriptive
4. **Run tests** — `dotnet test` must pass with zero failures
5. **Build clean** — `dotnet build` must complete with zero errors
6. **Push** your branch and open a PR against `master`
7. Fill in the PR template with a summary and test plan

## Issue Guidelines

- Search existing issues before opening a new one
- Use a clear, descriptive title
- Include your OS version, .NET version, and GameShift version
- For bugs, include steps to reproduce and expected vs actual behavior
- For feature requests, describe the use case and why it would benefit users
