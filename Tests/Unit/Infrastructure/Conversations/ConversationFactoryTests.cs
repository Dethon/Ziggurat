using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Infrastructure.Conversations;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Conversations;

public class ConversationFactoryTests
{
    [Fact]
    public async Task CreateAsync_PersistsTopicAndReturnsMatchingIdentity()
    {
        var now = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new Mock<IThreadStateStore>();
        TopicMetadata? saved = null;
        store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback<TopicMetadata, bool>((t, _) => saved = t)
            .Returns(Task.CompletedTask);

        var sut = new ConversationFactory(store.Object, clock);

        var creation = await sut.CreateAsync(new CreateConversationParams
        {
            AgentId = "agent-1",
            TopicName = "household @ Kitchen",
            Sender = "household",
            InitialPrompt = "what time is it"
        });

        saved.ShouldNotBeNull();
        saved.AgentId.ShouldBe("agent-1");
        saved.Name.ShouldBe("household @ Kitchen");
        saved.SpaceSlug.ShouldBe("default");
        saved.CreatedAt.ShouldBe(now);
        saved.LastMessageAt.ShouldBeNull();

        creation.Topic.ShouldBe(saved);
        creation.Identity.ChatId.ShouldBe(saved.ChatId);
        creation.Identity.ThreadId.ShouldBe(saved.ThreadId);
        creation.Identity.ConversationId.ShouldBe($"{saved.ChatId}:{saved.ThreadId}");
    }
}