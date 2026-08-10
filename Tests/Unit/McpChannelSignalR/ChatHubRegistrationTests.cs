using McpChannelSignalR.Modules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public sealed class ChatHubRegistrationTests
{
    // A phone's file picker holds the page down while the person chooses, and a frozen page sends
    // no keepalive. SignalR's default gives that thirty seconds before it drops the connection,
    // which is less time than picking a photo takes. The browser already waits minutes for the
    // server before giving up on it; this is the same tolerance from the other side.
    [Fact]
    public void APageHeldFrozenByAFilePicker_KeepsItsConnection()
    {
        var services = new ServiceCollection().AddLogging().AddChatSignalR();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<HubOptions>>();

        options.Value.ClientTimeoutInterval.ShouldNotBeNull();
        options.Value.ClientTimeoutInterval!.Value.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2));
    }
}