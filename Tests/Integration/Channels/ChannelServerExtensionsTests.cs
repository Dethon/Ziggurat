using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// The registration seam. One call wires the inbox, the transport tool, the error filter and the
// emitter, so a new transport supplies only its reply-sending logic. These assertions are about
// the shared call itself; each real server's use of it is pinned by ChannelReceiveContractTests.
public class ChannelServerExtensionsTests
{
    [Fact]
    public void AddChannelServer_RegistersTheInboxTheEmitterAndTheTransportTool()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddChannelServer(DeliveryPolicy.Broadcast);

        using var provider = services.BuildServiceProvider();

        provider.GetService<ChannelInbox>().ShouldNotBeNull();
        provider.GetRequiredService<ChannelNotificationEmitter>().Policy.ShouldBe(DeliveryPolicy.Broadcast);
        provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name)
            .ShouldContain(ChannelProtocol.ReceiveTool);
    }

    // McpChannelTelegram and McpChannelServiceBus reference Domain and this project alone. A
    // reference from here to Infrastructure would hand both of them a browser automation library,
    // a cache client, a printing library, a console UI toolkit and the whole agent stack as
    // transitive dependencies, and nothing else in the build would object.
    [Fact]
    public void McpHosting_ReferencesNothingFromInfrastructure() =>
        typeof(ChannelServerExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ShouldNotContain("Infrastructure");

    [Fact]
    public void AddChannelServer_BufferAlwaysWithoutASubscriberId_ThrowsAtRegistration() =>
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddMcpServer().AddChannelServer(DeliveryPolicy.BufferAlways));

    [Fact]
    public void AddChannelServer_BroadcastWithASubscriberId_ThrowsAtRegistration() =>
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddMcpServer().AddChannelServer(DeliveryPolicy.Broadcast, "channel-x"));

    private sealed record ProbeSettings(string Name);

    // A channel server has one inbox and one delivery policy. The call-tool filter already guards
    // itself, so a second call used to leave the filter alone and silently swap the emitter — last
    // policy wins, and a server that meant Broadcast would start gating on live subscribers. Today
    // it only fails incidentally, on the duplicate channel_receive tool name, which says nothing
    // about the policy that was lost.
    [Fact]
    public void AddChannelServer_CalledTwice_FailsInsteadOfSwappingThePolicy()
    {
        var builder = new ServiceCollection().AddMcpHost(new ProbeSettings("probe"));
        builder.AddChannelServer(DeliveryPolicy.Broadcast);

        Should.Throw<InvalidOperationException>(
                () => builder.AddChannelServer(DeliveryPolicy.GateOnLive))
            .Message.ShouldContain("AddChannelServer");
    }

    // The rule the six copies of this filter all had to state for themselves: a long poll ends in
    // cancellation whenever the agent hangs up or the server shuts down, and mapping that to an
    // error result would hand the agent's pump something to retry on. What decides that is the
    // request's own token, not the exception's runtime type — an OperationCanceledException the
    // tool threw on its own, with the caller's token untouched, is an ordinary failure.
    //
    // Asserted through a marked error mapper rather than by expecting a throw: the SDK's own outer
    // handler catches whatever the filter rethrows and answers with a generic message of its own,
    // so "did this exception reach the filter's error path" is the question that can be observed
    // over the wire — and it is the question the rule is about.
    [Fact]
    public async Task CallToolFilter_AnUncancelledOperationCanceledException_BecomesTheFiltersErrorResult()
    {
        await using var server = await StartAsync(Marked);

        var cancelled = await server.Client.CallToolAsync("cancels");

        InMemoryMcpServer.Text(cancelled).ShouldContain(Marker);
    }

    // An HttpClient timeout throws TaskCanceledException with the same shape as a genuine
    // cancellation, but the request's own token was never cancelled — this must not escape the
    // filter the way a real caller cancellation does.
    [Fact]
    public async Task CallToolFilter_ATaskCanceledExceptionWithNoCallerCancellation_BecomesTheFiltersErrorResult()
    {
        await using var server = await StartAsync(Marked);

        var timedOut = await server.Client.CallToolAsync("times_out");

        InMemoryMcpServer.Text(timedOut).ShouldContain(Marker);
    }

    // Only a genuine caller cancellation — the request's own token tripping — is exempt from the
    // filter's error mapping. Proven server-side: the mapper must never run once the caller who
    // issued the request is gone, regardless of what the client observes about a dead connection.
    [Fact]
    public async Task CallToolFilter_ACancelledRequestToken_NeverReachesTheErrorMapper()
    {
        var mapped = false;
        var server = await StartAsync(ex =>
        {
            mapped = true;
            return Marked(ex);
        });

        var hangingCall = server.Client.CallToolAsync("hangs").AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await server.Client.DisposeAsync();

        await Should.ThrowAsync<Exception>(() => hangingCall);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        mapped.ShouldBeFalse();

        await server.App.StopAsync();
        await server.App.DisposeAsync();
    }

    private const string Marker = "mapped-by-the-channel-filter";

    private static CallToolResult Marked(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{Marker}: {ex.Message}" }]
    };

    [Fact]
    public async Task CallToolFilter_AnyOtherException_BecomesAnErrorResult()
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync("throws");

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().First().Text.ShouldContain("boom");
    }

    [Fact]
    public async Task ChannelReceive_DeliversWhatTheEmitterPutOnTheInbox()
    {
        await using var server = await StartAsync();
        var emitter = server.App.Services.GetRequiredService<ChannelNotificationEmitter>();

        await Poll(server.Client, maxWaitMs: 0);
        await emitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "user",
            Content = "hello"
        });

        var batch = await Poll(server.Client, maxWaitMs: 0);

        batch.Items.Count.ShouldBe(1);
        batch.Items[0].Message!.Content.ShouldBe("hello");
    }

    private const string SubscriberId = ChannelProtocol.ChannelClientNamePrefix + "test";

    private static async Task<ChannelReceiveResult> Poll(McpClient client, int maxWaitMs)
    {
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?> { ["subscriberId"] = SubscriberId, ["maxWaitMs"] = maxWaitMs });

        var text = call.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions)!;
    }

    private static Task<RunningServer> StartAsync(Func<Exception, CallToolResult>? errorResult = null) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .AddChannelServer(DeliveryPolicy.Broadcast, errorResult: errorResult));
}