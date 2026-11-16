# 02: An agent test seam over the real tool loop

**What to build:** A prefactor that adds the agent test seam the spec chose. It drives the chat monitor through the fake channel connection, as the monitor tests do. Its agent runs the real tool-loop approval client over a scripted model and a local tool, instead of a fake agent, so one test can observe both a conversation group's routing and what the tool loop does.

Use the seam to pin, from the library itself, how the function-invoking loop treats a request to terminate:
- in the concurrent path, which this client uses;
- in the sequential path;
- in particular, whether every call of a batch still has its result stored.

Steering (ticket 07) relies on that answer.

**Blocked by:** None (can start immediately).

**Status:** done

- [x] A test sends a person's message through the fake channel connection, the scripted model calls the local tool, and the test observes the replies on the channel and the stored line (user message, call, result, final text).
- [x] A test pins what terminating from inside a tool call does when a batch runs concurrently: every call's result is stored and no further model call is made. If the library behaves otherwise, the test states what it does instead.
- [x] The same is pinned for a sequential batch.
- [x] The seam runs without a network, a real model or Docker.

## Findings

The seam is `ToolLoopRun` (`Tests/Unit/Domain/Monitor/ToolLoopRun.cs`): the real `ChatMonitor` over a `FakeChannelConnection`, whose agent factory builds the real `McpAgent` over the real `ToolApprovalChatClient`, with a `ScriptedModel` as the model, `LocalTool`s as domain tools and the real `RedisChatMessageStore` over an in-memory `IThreadStateStore`. `McpAgent` was chosen over a bare `ChatClientAgent` because with no server in its definition it dials nothing, the factory has to return a `DisposableAgent` anyway, and it is where production builds the turn's run options (where 07's steering handle goes) and the stored line. Tools named in `refused` are left off the whitelist and the channel turns them down; `sequential: true` sets `AllowConcurrentInvocation = false` on the client. The tests are `ChatMonitorToolLoopTests`.

How Microsoft.Extensions.AI 11.0.0 treats `FunctionInvocationContext.Terminate` (read from the net11.0 build, `FunctionInvocationProcessor.ProcessFunctionCallsAsync` and `FunctionInvokingChatClient.GetStreamingResponseAsync`, and pinned by the tests):

- **When the flag is read.** The processor copies `context.Terminate` into the call's result once `InvokeFunctionAsync` has returned, or has thrown and the exception was captured. Setting it anywhere inside the `InvokeFunctionAsync` override, before or after the base invocation, is the same to the library. The tests set it through a turned-down approval, the hook the approval client already has. 07 sets the same flag in the same override.
- **Concurrent path** (`AllowConcurrentInvocation` and more than one call, which is what `ToolApprovalChatClient` sets). Every call of the batch is started and awaited with `Task.WhenAll`. Only then does the client build a result message for each call, add it to the history, yield it on the stream, and leave the loop before the next model call. **Every call keeps its result and the model is not asked again**, wherever in the batch the terminating call was written; the test pins positions 0, 1 and 2. A sibling still running when the terminating call returned is waited for, and the turn does not end until it returns. The turn completes on the channel with `StreamComplete` alone, since it said nothing, and its line ends in tool results. That it is not marked interrupted or failed comes from reading `RedisChatMessageStore`, not from a test; 07 pins it ("the cut turn neither interrupted nor failed").
- **Sequential path** (concurrency off). Calls run in written order, and the loop `break`s right after the first result that carries the flag. **Calls written after it never run and get no result**, but the assistant message that wrote them is stored whole. The stored line then holds calls with no results, and the next request would be a provider 400. Calls before it keep their results, and the model is not asked again.
- **A batch of one call** takes the sequential branch even with concurrency on, because the concurrent branch needs `Count > 1`. It is safe all the same: the terminating call is the only call, and it has its result.
- **For 07.** Signalling the handle from inside the approval client is safe as long as the client keeps `AllowConcurrentInvocation = true`, which it sets in its constructor for every agent and worker. No workaround is needed in that case. Turning concurrency off anywhere would leave dangling calls, so 07 must not.
