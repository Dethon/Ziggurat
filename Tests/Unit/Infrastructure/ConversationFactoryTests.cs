using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Infrastructure.Conversations;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A topic is labelled with the name its owner went by, which a sees-all reader of a private topic
// and everyone in a shared one see beside it. A schedule's or a watch's fire opens its person's topic
// with the service's sender, which is no name of theirs: the label read "scheduler".
public class ConversationFactoryTests
{
    private static readonly Principal _carmen = new("scheduling", "carmen", new Account("home", "u-carmen"));

    [Theory]
    [InlineData(ServiceSenders.Scheduler)]
    [InlineData(ServiceSenders.Watch)]
    [InlineData(ServiceSenders.Login)]
    public async Task CreateAsync_ForAPersonsFire_NamesNoServiceAsTheirName(string service)
    {
        var saved = await CreateAsync(service);

        (saved.OwnerId, saved.OwnerName).ShouldBe(("u-carmen", (string?)null));
    }

    [Fact]
    public async Task CreateAsync_ForAPersonsOwnTurn_KeepsTheNameTheyWentBy()
    {
        var saved = await CreateAsync("Carmen");

        (saved.OwnerId, saved.OwnerName).ShouldBe(("u-carmen", "Carmen"));
    }

    private static async Task<TopicMetadata> CreateAsync(string sender)
    {
        TopicMetadata? saved = null;
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback<TopicMetadata, bool>((t, _) => saved = t)
            .Returns(Task.CompletedTask);

        await new ConversationFactory(store.Object, TimeProvider.System).CreateAsync(new CreateConversationParams
        {
            AgentId = "jonas", TopicName = "Scheduled task", Sender = sender, Principal = _carmen
        });

        return saved.ShouldNotBeNull();
    }
}