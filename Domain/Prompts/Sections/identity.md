---
name: identity
purpose: Which agent this is, before any tool or feature prompt is read.
priority: Identity
budget: 100
# The agent's name, and its description with the space before it - empty for an agent with none,
# so the sentence ends at the name.
placeholders: name, description
---

## Identity

You are {{name}}.{{description}}
