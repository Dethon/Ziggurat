# 11: A reply cut off by the output limit is marked

Status: ready-for-agent
Blocked by: 10
Spec: ../spec.md (Cut-off replies)

## What
- Read the finish reason of a turn's last model call where the stream's updates are collected (`ChatResponseUpdate.FinishReason` through `OpenRouterChatClient`). Check that the Responses adapter surfaces `length` or `incomplete`.
- Mark the stored reply `Interrupted`, as ticket 10 does.

## Acceptance
A test with a stubbed stream that ends on length shows the reply marked.
