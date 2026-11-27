using Domain.DTOs.Channel;
using McpServerApps.Asks;
using McpServerApps.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// An ask's conversation is continued only by the app and the viewer that opened it, within the follow-up
// window the server is set to, and only while the app's building agent is the one that holds its history.
public sealed class AskConversationsTests
{
    private static readonly Account _her = new("household-home", "user-her");
    private static readonly Account _him = new("household-home", "user-him");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly AppsSettings _settings = new() { AppsDomain = "apps.test", Asks = new AppAskSettings { FollowUpWindow = TimeSpan.FromHours(3) } };

    [Fact]
    public void TheAppAndViewerThatOpenedIt_ContinueIt_OnTheSameAgent()
    {
        var conversations = new AskConversations(_clock, _settings);
        var conversation = conversations.Open("app-1", _her, "jonas");

        conversations.TryContinue("app-1", _her, "jonas", conversation).ShouldBeTrue();
        conversations.TryContinue("app-2", _her, "jonas", conversation).ShouldBeFalse();
        conversations.TryContinue("app-1", _him, "jonas", conversation).ShouldBeFalse();
    }

    // The agent groups an app's messages by conversation and agent, so another agent holds none of the
    // conversation's history: the app starts afresh rather than being answered as though it held it.
    [Fact]
    public void AnAppWhoseBuildingAgentChanged_StartsAfresh()
    {
        var conversations = new AskConversations(_clock, _settings);
        var conversation = conversations.Open("app-1", _her, "jonas");

        conversations.TryContinue("app-1", _her, "nabu", conversation).ShouldBeFalse();
    }

    [Fact]
    public void TheWindow_IsTheServersSetting()
    {
        var conversations = new AskConversations(_clock, _settings);
        var conversation = conversations.Open("app-1", _her, "jonas");

        _clock.Advance(_settings.Asks.FollowUpWindow - TimeSpan.FromMinutes(1));
        conversations.TryContinue("app-1", _her, "jonas", conversation).ShouldBeTrue();
        _clock.Advance(_settings.Asks.FollowUpWindow);

        conversations.TryContinue("app-1", _her, "jonas", conversation).ShouldBeFalse();
    }
}