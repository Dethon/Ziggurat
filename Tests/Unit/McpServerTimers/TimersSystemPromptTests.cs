using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Prompts;
using McpServerTimers.McpPrompts;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class TimersSystemPromptTests
{
    private sealed class StubCatalog(
        Func<CancellationToken, Task<IReadOnlyList<SatelliteDescriptor>>> getAll) : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) => getAll(ct);

        public Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task GetTimerPrompt_EmbedsTheHubRoster()
    {
        var sut = new TimersSystemPrompt(new StubCatalog(_ =>
                Task.FromResult<IReadOnlyList<SatelliteDescriptor>>([new("kitchen-01", "Kitchen")])),
            new FakeTimeProvider());

        var prompt = await sut.GetTimerPrompt(CancellationToken.None);

        prompt.ShouldContain("kitchen-01");
        prompt.ShouldContain("Kitchen");
    }

    [Fact]
    public async Task GetTimerPrompt_HubUnreachable_FailsOpenToTheRosterlessPrompt()
    {
        // MCP prompts are fetched while the agent session is built — an unreachable hub must
        // degrade to the roster-less static text, never stall or fail session build (the roster
        // stays discoverable in-conversation via create-time errors).
        var sut = new TimersSystemPrompt(new StubCatalog(_ =>
            throw new VoiceHubUnavailableException("connection refused")), new FakeTimeProvider());

        var prompt = await sut.GetTimerPrompt(CancellationToken.None);

        prompt.ShouldBe(TimerPrompt.Prompt);
    }

    [Fact]
    public async Task GetTimerPrompt_HungHub_FailsOpenAtTheRosterTimeout()
    {
        // The named client's 15s timeout is fine for tool calls but far too long to block session
        // build on — the prompt fetch carries its own much shorter cap.
        var time = new FakeTimeProvider();
        var sut = new TimersSystemPrompt(new StubCatalog(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, ct);
            return [];
        }), time);

        var task = sut.GetTimerPrompt(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3));

        // Real-time guard so a missing roster cap fails the test instead of hanging the run.
        (await task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(TimerPrompt.Prompt);
    }
}