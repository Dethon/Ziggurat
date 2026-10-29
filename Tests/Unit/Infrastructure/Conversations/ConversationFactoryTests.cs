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

    // Whose a topic is comes from the turn that opened it: a person's account gives a topic that
    // person owns, private until they share it, under the name they went by.
    [Fact]
    public async Task CreateAsync_ForAPersonsTurn_GivesATopicTheyOwn()
    {
        var (sut, saved) = Build();

        await sut.CreateAsync(Opening(new Principal("voice", "u-fran", new Account("t-home", "u-fran")), "Fran"));

        var topic = saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName).ShouldBe(("u-fran", "t-home", false, "Fran"));
    }

    // A turn that acts for no account — the voice guest's — gives a topic the household shares.
    [Fact]
    public async Task CreateAsync_ForATurnActingForNoAccount_GivesATopicTheHouseholdShares()
    {
        var (sut, saved) = Build();

        await sut.CreateAsync(Opening(new Principal("voice", "household"), "household"));

        var topic = saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.Shared, topic.OwnerName).ShouldBe(((string?)null, true, (string?)null));
    }

    private static (ConversationFactory Sut, List<TopicMetadata> Saved) Build()
    {
        var saved = new List<TopicMetadata>();
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback<TopicMetadata, bool>((t, _) => saved.Add(t))
            .Returns(Task.CompletedTask);
        return (new ConversationFactory(store.Object, new FakeTimeProvider()), saved);
    }

    private static CreateConversationParams Opening(Principal principal, string sender) => new()
    {
        AgentId = "agent-1", TopicName = "Kitchen", Sender = sender, Principal = principal
    };
}