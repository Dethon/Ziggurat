using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Integration.McpServers;

// fetch_attachment as the agent calls it: a file a channel server stored for a household comes back
// only on a call the agent signed for somebody of that household, and a file stored for none comes
// back on any call, the nobody-signed ones Sugary's fetches are included.
public sealed class UploadStoreFetchToolTests : IAsyncLifetime
{
    private sealed record Storage(string StoragePath) : IUploadStoreSettings;

    private sealed record ChannelSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"channel-uploads-{Guid.NewGuid():N}");
    private RunningServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ChannelSettings())
            .AddUploadStore(new Storage(_root), new RetentionSettings()));

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AHouseholdsFile_IsFetchedOnlyOnACallForThatHousehold()
    {
        var stored = await Store().SaveForHouseholdAsync(
            "household-a", "conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await FetchAsync(stored.Id, new Account("household-a", "ana"))).ShouldBe(Convert.ToBase64String([1, 2, 3]));
        (await FetchAsync(stored.Id, new Account("household-b", "bea"))).ShouldBeEmpty();
        (await FetchAsync(stored.Id, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileStoredForNone_IsFetchedOnACallForNobody()
    {
        var stored = await Store().SaveAsync("conversation", "photo.jpg", "image/jpeg", [4, 5], CancellationToken.None);

        (await FetchAsync(stored.Id, null)).ShouldBe(Convert.ToBase64String([4, 5]));
    }

    private UploadStore Store() => _server.App.Services.GetRequiredService<UploadStore>();

    private async Task<string> FetchAsync(string attachmentId, Account? person)
    {
        var request = new CallToolRequestParams
        {
            Name = ChannelProtocol.FetchAttachmentTool,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["attachmentId"] = JsonSerializer.SerializeToElement(attachmentId)
            }
        };
        if (person is not null)
        {
            var turn = new ConversationContext("agent", "conversation", person.UserId, new ReplyTarget(ChannelIds.Sugary, "conversation"))
            {
                Principal = new Principal(ChannelIds.Sugary, person.UserId, person)
            };
            request.Meta = new JsonObject
            {
                [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
            };
        }

        var result = await _server.Client.CallToolAsync(request);
        result.IsError.ShouldNotBe(true);
        return InMemoryMcpServer.Text(result);
    }
}