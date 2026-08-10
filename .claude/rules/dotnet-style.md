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
