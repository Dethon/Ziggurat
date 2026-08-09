using Domain.Agents;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.StateManagers;
using McpChannelSignalR.Services;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelSignalR;

public class RedisStateServiceTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private readonly RedisStateService _sut = new(redis.Connection);

    [Fact]
    public async Task GetAllTopicsAsync_FiltersBySpaceSlug()
    {
        var topic1 = new TopicMetadata("t-s1", 300, 0, "agent-slug", "Space1", DateTimeOffset.UtcNow, null, SpaceSlug: "space-a");
        var topic2 = new TopicMetadata("t-s2", 301, 0, "agent-slug", "Space2", DateTimeOffset.UtcNow, null, SpaceSlug: "space-b");

        await _sut.SaveTopicAsync(topic1);
        await _sut.SaveTopicAsync(topic2);

        var filtered = await _sut.GetAllTopicsAsync("agent-slug", "space-a");
        filtered.ShouldContain(t => t.TopicId == "t-s1");
        filtered.ShouldNotContain(t => t.TopicId == "t-s2");
    }

    [Fact]
    public async Task GetHistoryAsync_ReadsNewRedisListFormat()
    {
        const string agentId = "agent-hist";
        const long chatId = 900;
        const long threadId = 0;
        var key = new AgentKey($"{chatId}:{threadId}", agentId).ToString();

        // The agent now persists history as a Redis List via RedisThreadStateStore.
        var store = new RedisThreadStateStore(redis.Connection, TimeSpan.FromMinutes(5));
        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "hello there"),
            new ChatMessage(ChatRole.Assistant, "hi, how can I help?")
        ]);

        var history = await _sut.GetHistoryAsync(agentId, chatId, threadId);

        history.Select(h => h.Content).ShouldBe(["hello there", "hi, how can I help?"]);
    }

    // The read keeps only text and used to discard a message whose text was empty, which would
    // make an image-only message vanish on reload. The transcript is a record of what was sent.
    [Fact]
    public async Task GetHistoryAsync_AMessageWithAttachmentsAndNoText_SurvivesTheRead()
    {
        const string agentId = "agent-attach";
        const long chatId = 901;
        const long threadId = 0;
        var key = new AgentKey($"{chatId}:{threadId}", agentId).ToString();

        var photo = new AttachmentReference
        {
            Id = "901-0/abc",
            FileName = "photo.png",
            MediaType = "image/png",
            SizeBytes = 4
        };
        var message = new ChatMessage(ChatRole.User, "");
        message.SetAttachments([photo]);

        var store = new RedisThreadStateStore(redis.Connection, TimeSpan.FromMinutes(5));
        await store.AppendMessagesAsync(key, [message]);

        var history = await _sut.GetHistoryAsync(agentId, chatId, threadId);

        var read = history.ShouldHaveSingleItem();
        read.Content.ShouldBeNullOrEmpty();
        read.Attachments.ShouldNotBeNull();
        read.Attachments!.Single().FileName.ShouldBe("photo.png");
    }

    [Fact]
    public async Task GetHistoryAsync_ProjectsAttachmentsAlongsideTheText()
    {
        const string agentId = "agent-attach-text";
        const long chatId = 902;
        const long threadId = 0;
        var key = new AgentKey($"{chatId}:{threadId}", agentId).ToString();

        var message = new ChatMessage(ChatRole.User, "what is in this?");
        message.SetAttachments([
            new AttachmentReference
            {
                Id = "902-0/def", FileName = "scan.pdf", MediaType = "application/pdf", SizeBytes = 9
            }
        ]);

        var store = new RedisThreadStateStore(redis.Connection, TimeSpan.FromMinutes(5));
        await store.AppendMessagesAsync(key, [message]);

        var history = await _sut.GetHistoryAsync(agentId, chatId, threadId);

        var read = history.ShouldHaveSingleItem();
        read.Content.ShouldBe("what is in this?");
        read.Attachments!.Single().MediaType.ShouldBe("application/pdf");
    }
}