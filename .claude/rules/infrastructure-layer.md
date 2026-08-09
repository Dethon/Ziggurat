---
paths:
  - "Infrastructure/**/*.cs"
---

# Infrastructure Layer Rules

This layer implements Domain interfaces and handles external concerns.

- NEVER import from the `Agent` namespace — Agent handles DI and bootstrapping, not Infrastructure.
- Implement a Domain interface when the service is consumed by Domain; single-implementation services not consumed by Domain do not need one.
