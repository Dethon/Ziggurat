using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A model writes several calls in one message and some of them depend on each other: a timer's
// directory removed and its file created again, two clicks on one tab. They used to all start
// at once, and which one won was a race the model could not see. Every tool here is held open
// by the test, so "in flight together" and "only after" are observed rather than timed.
public class ToolApprovalChatClientWrittenOrderTests
{
    private static readonly SharedResource _pasta = SharedResource.Of("filesystem", "/timers", "pasta");
    private static readonly SharedResource _pastaFile = _pasta.Under("timer.json");
    private static readonly SharedResource _eggs = SharedResource.Of("filesystem", "/timers", "eggs");

    [Fact]
    public async Task AWriteUnderADirectoryWrittenAfterItsRemoval_StartsOnlyOnceTheRemovalHasFinished()
    {
        var remove = new HeldTool("remove", CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        var turn = Run(remove, create);

        await remove.Started;
        await Eventually.Settle();
        create.HasStarted.ShouldBeFalse("the create depends on the removal written before it");

        remove.Finish();
        await create.Started;
        create.Finish();
        await turn;
    }

    [Fact]
    public async Task TwoWritesToUnrelatedThings_AreInFlightTogether()
    {
        var pasta = new HeldTool("pasta", CallFootprint.Changing(_pasta));
        var eggs = new HeldTool("eggs", CallFootprint.Changing(_eggs));
        var turn = Run(pasta, eggs);

        await Task.WhenAll(pasta.Started, eggs.Started);

        eggs.Finish();
        pasta.Finish();
        await turn;
    }

    [Fact]
    public async Task TwoReadsOfOneThing_AreInFlightTogether()
    {
        var first = new HeldTool("first", CallFootprint.Reading(_pasta));
        var second = new HeldTool("second", CallFootprint.Reading(_pasta));
        var turn = Run(first, second);

        await Task.WhenAll(first.Started, second.Started);

        first.Finish();
        second.Finish();
        await turn;
    }

    [Fact]
    public async Task AReadWrittenAfterAWriteOfTheSameThing_WaitsForTheWrite()
    {
        var write = new HeldTool("write", CallFootprint.Changing(_pastaFile));
        var read = new HeldTool("read", CallFootprint.Reading(_pastaFile));
        var turn = Run(write, read);

        await write.Started;
        await Eventually.Settle();
        read.HasStarted.ShouldBeFalse("the read was written after the write it would otherwise race");

        write.Finish();
        await read.Started;
        read.Finish();
        await turn;
    }

    [Fact]
    public async Task AReadBesideAnUnrelatedWrite_IsInFlightWithIt()
    {
        var write = new HeldTool("write", CallFootprint.Changing(_pasta));
        var read = new HeldTool("read", CallFootprint.Reading(_eggs));
        var turn = Run(write, read);

        await Task.WhenAll(write.Started, read.Started);

        write.Finish();
        read.Finish();
        await turn;
    }

    // Declaring nothing is not declaring "nothing shared": a tool nobody has described may touch
    // whatever its neighbours touch, so it runs alone against all of them, in either position.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AToolThatDeclaresNoFootprint_IsOrderedAgainstEverythingThatTouchesAnything(bool undeclaredFirst)
    {
        var undeclared = new HeldTool("undeclared", footprint: null);
        var read = new HeldTool("read", CallFootprint.Reading(_eggs));
        var (first, second) = undeclaredFirst ? (undeclared, read) : (read, undeclared);
        var turn = Run(first, second);

        await first.Started;
        await Eventually.Settle();
        second.HasStarted.ShouldBeFalse();

        first.Finish();
        await second.Started;
        second.Finish();
        await turn;
    }

    // The valuable concurrent case: a skill load shares nothing with anything, so it runs beside
    // even a tool that has said nothing about itself.
    [Fact]
    public async Task ACallThatTouchesNothing_IsInFlightWithAToolThatDeclaresNoFootprint()
    {
        var undeclared = new HeldTool("undeclared", footprint: null);
        var load = new HeldTool("load", CallFootprint.Nothing);
        var turn = Run(undeclared, load);

        await Task.WhenAll(undeclared.Started, load.Started);

        load.Finish();
        undeclared.Finish();
        await turn;
    }

    [Fact]
    public async Task WhereOnlyTheFirstAndThirdConflict_TheSecondRunsAlongsideBoth()
    {
        var first = new HeldTool("first", CallFootprint.Changing(_pasta));
        var second = new HeldTool("second", CallFootprint.Changing(_eggs));
        var third = new HeldTool("third", CallFootprint.Changing(_pastaFile));
        var turn = Run(first, second, third);

        await Task.WhenAll(first.Started, second.Started);
        await Eventually.Settle();
        third.HasStarted.ShouldBeFalse("the third conflicts with the first, which is still running");

        first.Finish();
        await third.Started;
        second.HasFinished.ShouldBeFalse("the second was in flight with the first and is now in flight with the third");

        second.Finish();
        third.Finish();
        await turn;
    }

    [Fact]
    public async Task EachResult_ComesBackUnderItsOwnCallId_WhateverOrderTheCallsFinishedIn()
    {
        var first = new HeldTool("first", CallFootprint.Changing(_pasta));
        var second = new HeldTool("second", CallFootprint.Changing(_eggs));
        var third = new HeldTool("third", CallFootprint.Changing(_pastaFile));
        var turn = Run(first, second, third);

        await Task.WhenAll(first.Started, second.Started);
        second.Finish();
        first.Finish();
        await third.Started;
        third.Finish();
        var response = await turn;

        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => (r.CallId, Result: r.Result?.ToString()))
            .ShouldBe([("call-first", "first done"), ("call-second", "second done"), ("call-third", "third done")]);
    }

    // A call naming a tool nothing serves is answered without ever being invoked, so it can never
    // finish: a call written after it must not be made to wait for that.
    [Fact]
    public async Task ACallNamingNoTool_DoesNotHoldUpTheCallsWrittenAfterIt()
    {
        var write = new HeldTool("write", CallFootprint.Changing(_pasta));
        var inner = new ScriptedChatClient(
            new FunctionCallContent("call-ghost", "mcp__t__ghost", new Dictionary<string, object?>()),
            write.Call);
        var turn = Client(inner).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [write.Tool] });

        await write.Started;
        write.Finish();
        await turn;
    }

    [Fact]
    public async Task ACallWrittenAfterOneThatThrew_StillRuns()
    {
        var failing = FootprintDeclaration.Declared(
            AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"), "mcp__t__failing"),
            CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        var inner = new ScriptedChatClient(
            new FunctionCallContent("call-failing", "mcp__t__failing", new Dictionary<string, object?>()),
            create.Call);
        var turn = Client(inner).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [failing, create.Tool] });

        await create.Started;
        create.Finish();
        await turn;
    }

    [Fact]
    public async Task ACallWrittenAfterOneThePersonRejected_StillRuns()
    {
        var remove = new HeldTool("remove", CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        var handler = new PerToolApprovalHandler(tool =>
            tool == remove.Tool.Name ? ToolApprovalResult.Rejected : ToolApprovalResult.Approved);
        var turn = new ToolApprovalChatClient(new ScriptedChatClient(remove.Call, create.Call), handler, "conv")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "go")], Offering(remove, create));

        await create.Started;
        create.Finish();
        await turn;

        remove.HasStarted.ShouldBeFalse();
    }

    // The question is about the call, not about its turn in line: a person is asked the moment
    // the model wrote it, and what waits for the earlier call is the running.
    [Fact]
    public async Task ACallWaitingItsTurn_IsAskedAboutWithoutWaiting()
    {
        var remove = new HeldTool("remove", CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var turn = new ToolApprovalChatClient(new ScriptedChatClient(remove.Call, create.Call), handler, "conv")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "go")], Offering(remove, create));

        await remove.Started;
        await Eventually.Until(() => Asked(handler).Contains(create.Tool.Name), "the create is asked about while the removal runs");
        create.HasStarted.ShouldBeFalse();

        remove.Finish();
        await create.Started;
        create.Finish();
        await turn;
    }

    [Fact]
    public async Task CancellingTheTurn_ReleasesACallStillWaitingItsTurn_WithoutRunningIt()
    {
        var remove = new HeldTool("remove", CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        using var cancellation = new CancellationTokenSource();
        var turn = Client(new ScriptedChatClient(remove.Call, create.Call))
            .GetResponseAsync([new ChatMessage(ChatRole.User, "go")], Offering(remove, create), cancellation.Token);

        await remove.Started;
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => turn);
        create.HasStarted.ShouldBeFalse();
    }

    // Production streams. The streamed path hands the batch to the same machinery, and this pins
    // that it does.
    [Fact]
    public async Task OverAStreamedMessage_ConflictingCallsStillRunInWrittenOrder()
    {
        var remove = new HeldTool("remove", CallFootprint.Changing(_pasta));
        var create = new HeldTool("create", CallFootprint.Changing(_pastaFile));
        var client = Client(new ScriptedChatClient(remove.Call, create.Call));
        var turn = Drain(client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], Offering(remove, create)));

        await remove.Started;
        await Eventually.Settle();
        create.HasStarted.ShouldBeFalse();

        remove.Finish();
        await create.Started;
        create.Finish();
        await turn;
    }

    // A later message is a later batch: nothing an earlier one left behind may hold it up.
    [Fact]
    public async Task TheNextMessagesCalls_AreNotHeldUpByTheLastMessages()
    {
        var first = new HeldTool("first", CallFootprint.Changing(_pasta));
        var second = new HeldTool("second", CallFootprint.Changing(_pasta));
        var inner = new ScriptedChatClient([[first.Call], [second.Call]]);
        var turn = Client(inner).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], Offering(first, second));

        await first.Started;
        first.Finish();
        await second.Started;
        second.Finish();
        await turn;
    }

    private static Task<ChatResponse> Run(params HeldTool[] written) =>
        Client(new ScriptedChatClient([.. written.Select(t => t.Call)]))
            .GetResponseAsync([new ChatMessage(ChatRole.User, "go")], Offering(written));

    private static ToolApprovalChatClient Client(IChatClient inner) =>
        new(inner, new TestApprovalHandler(ToolApprovalResult.Approved), "conv", whitelistPatterns: ["mcp__t__*"]);

    private static ChatOptions Offering(params HeldTool[] tools) =>
        new() { Tools = [.. tools.Select(t => t.Tool)] };

    private static IReadOnlyList<string> Asked(TestApprovalHandler handler)
    {
        lock (handler.RequestedApprovals)
        {
            return [.. handler.RequestedApprovals.SelectMany(r => r).Select(r => r.ToolName)];
        }
    }

    private static async Task Drain(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates)
        {
        }
    }

    // A tool that starts when it is invoked and finishes when the test says so.
    private sealed class HeldTool
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldTool(string name, CallFootprint? footprint)
        {
            var function = AIFunctionFactory.Create(
                async (CancellationToken ct) =>
                {
                    _started.TrySetResult();
                    await _finish.Task.WaitAsync(ct);
                    return $"{name} done";
                },
                $"mcp__t__{name}");
            Tool = footprint is null ? function : FootprintDeclaration.Declared(function, footprint);
            Call = new FunctionCallContent($"call-{name}", Tool.Name, new Dictionary<string, object?>());
        }

        public AIFunction Tool { get; }
        public FunctionCallContent Call { get; }

        // Bounded, so a call that never starts fails the test that expected it rather than
        // hanging the run.
        public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public bool HasStarted => _started.Task.IsCompleted;
        public bool HasFinished => _finish.Task.IsCompleted;

        public void Finish() => _finish.TrySetResult();
    }

    // One message of calls per scripted step, then a plain answer.
    private sealed class ScriptedChatClient(IReadOnlyList<IReadOnlyList<FunctionCallContent>> messages) : IChatClient
    {
        private int _next;

        public ScriptedChatClient(params FunctionCallContent[] calls) : this([calls])
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(Next()));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var message = Next();
            yield return new ChatResponseUpdate(message.Role, message.Contents) { MessageId = "m" };
        }

        private ChatMessage Next()
        {
            var step = Interlocked.Increment(ref _next) - 1;
            return step < messages.Count
                ? new ChatMessage(ChatRole.Assistant, [.. messages[step]])
                : new ChatMessage(ChatRole.Assistant, "Done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class PerToolApprovalHandler(Func<string, ToolApprovalResult> decide) : IToolApprovalHandler
    {
        public Task<ToolApprovalResult> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken) =>
            Task.FromResult(decide(ask.Requests[0].ToolName));

        public Task NotifyAutoApprovedAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}