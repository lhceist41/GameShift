# Contributing to GameShift

Thank you for your interest in contributing to GameShift! This guide will help you get set up and submit your first pull request.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) 17.12+ (or VS Code with C# Dev Kit)
- Windows 10 21H2+ or Windows 11 22H2+ (x64)
- Git
- Administrator privileges (required to run and test optimizations)

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

# Run the application (requires administrator)
dotnet run --project src/GameShift.App

# Build the watchdog service
dotnet build src/GameShift.Watchdog
```

## Project Structure

```
src/
  GameShift.App/         # WPF application  - views, viewmodels, services, UI
  GameShift.Core/        # Core logic  - detection, optimization, profiles,
                         #   monitoring, journal, watchdog shared code
  GameShift.Watchdog/    # Windows Service  - heartbeat monitor, crash recovery
  GameShift.Tests/       # Unit tests (xUnit + Moq)
```

- **GameShift.App** is the UI layer. It depends on GameShift.Core but never the reverse.
- **GameShift.Core** contains all business logic with zero UI dependencies. This includes the state journal, optimization interfaces, revert logic, and watchdog shared components. Both the main app and the watchdog service reference this project.
- **GameShift.Watchdog** is a lightweight Windows Service that monitors the main app via named pipe heartbeat and handles crash recovery. It references GameShift.Core for journal and revert logic.
- **GameShift.Tests** covers Core logic. Tests must pass before any PR is merged.

## Coding Standards

- Follow the existing code style  - the project uses an `.editorconfig` for formatting
- Use `PascalCase` for public members, `_camelCase` for private fields
- Prefer `async/await` for I/O-bound operations
- Keep GameShift.Core free of WPF or UI references  - the watchdog service must be able to use any shared code without pulling in UI dependencies
- New optimizations must use the state journal (see below), not direct registry writes without backup

## Architecture: State Journal & Optimization Pattern

GameShift uses a command pattern with a state journal for all system modifications. Every change is recorded before it's made and can be deterministically reverted  - even by the watchdog service or boot recovery task after a crash.

### Core interfaces

**`IOptimization`**  - All optimizations implement this:

```csharp
public interface IOptimization
{
    string Name { get; }
    OptimizationCategory Category { get; }
    
    bool CanApply(SystemContext context);   // Pre-flight checks
    OptimizationResult Apply();             // Apply and return original + applied values
    OptimizationResult Revert();            // Restore original values
    bool Verify();                          // Confirm change is still in effect
}
```

**`IJournaledOptimization`**  - Optimizations that support crash recovery also implement this:

```csharp
public interface IJournaledOptimization
{
    void RevertFromRecord(string originalValueJson);  // Revert using journal data only
}
```

`RevertFromRecord` allows the watchdog service and boot recovery task to revert optimizations without instantiating the full optimization object with all its dependencies. It receives the serialized original values from the state journal and performs a minimal restore.

### Adding a new optimization

1. Create a new class in `src/GameShift.Core/Optimization/` implementing `IOptimization`
2. Implement `CanApply()` for pre-flight checks (OS version, hardware support, anti-cheat detection)
3. Implement `Apply()`  - read original values first, apply changes, return `OptimizationResult` with both original and applied values serialized as JSON
4. Implement `Revert()`  - restore original values from the in-memory state captured during `Apply()`
5. Implement `Verify()`  - re-read the system state and confirm the applied values are still in effect
6. Implement `IJournaledOptimization.RevertFromRecord()` for crash recovery support  - parse the JSON and restore values without any runtime dependencies
7. Register the module in `OptimizationEngine`
8. Add unit tests in `GameShift.Tests`

### Important conventions

- **Read before write.** Every registry key, service state, or system setting must be read and recorded before modification.
- **Verify after write.** Registry writes can silently fail (group policy overrides, another tool reverting). Always confirm.
- **LIFO revert order.** Optimizations are reverted in reverse order of application. `OptimizationEngine` handles this.
- **No UI in Core.** Optimization classes must not reference WPF types. The watchdog service calls the same revert code  - if it pulls in `PresentationFramework.dll`, something is wrong.
- **Graceful degradation.** If a feature requires a specific OS version, CPU architecture, or GPU vendor, gate it in `CanApply()` and skip silently. Never crash on unsupported hardware.

## Pull Request Process

1. **Fork** the repository
2. **Create a feature branch** from `master` (`git checkout -b feature/your-feature`)
3. **Make your changes**  - keep commits focused and descriptive
4. **Run tests**  - `dotnet test` must pass with zero failures
5. **Build clean**  - `dotnet build` must complete with zero errors and zero warnings
6. **Test with admin privileges**  - optimizations that modify system state need to be tested on a real Windows install, not just unit tested
7. **Push** your branch and open a PR against `master`
8. Fill in the PR template with a summary, what was tested, and which Windows versions were verified

## Issue Guidelines

- Search existing issues before opening a new one
- Use a clear, descriptive title
- Include your OS version (build number), .NET version, GPU vendor/model, and GameShift version
- For bugs: include steps to reproduce, expected vs actual behavior, and the activity log if relevant
- For feature requests: describe the use case and why it would benefit users
- For DPC/latency issues: include a screenshot or export from the DPC Doctor page
