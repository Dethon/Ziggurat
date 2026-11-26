---
paths:
  - "**/*.cs"
---

# .NET Coding Style

Formatting and mechanical style (file-scoped namespaces, `var`, braces) are owned by `.editorconfig` and the pre-commit `dotnet format` hook — follow the surrounding code.

- Primary constructors for DI: `public class Foo(IBar bar)`
- `record` types for DTOs and immutable data
- `ArgumentNullException.ThrowIfNull()` for guard clauses
- `IReadOnlyList<T>` / `IReadOnlyCollection<T>` for return types
- `TimeProvider` for testable time-dependent code

## LINQ Over Loops

**STRONGLY prefer LINQ over `for`/`foreach`/`while` loops.** A traditional loop needs a reason: unavoidable external mutation, control flow LINQ can't express cleanly, or a measured hot path.

## Documentation

- No XML documentation comments
- Only comment to explain "why", never "what"

## HTTP calls

- A catch for "the other side could not be reached" names `HttpRequestException or SocketException`: HttpClient now and then throws a reset as a bare `SocketException`, never wrapped (`PersonsAppsClientTests.AnAppsServerWhoseConnectionFailsBare_IsNoApps`). Never a bare `IOException`: HttpClient wraps its stream failures, and a missing file is an `IOException` too.
