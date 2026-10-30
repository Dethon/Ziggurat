using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients;

// A push names a topic, so who it reaches is who may know of it: the space for a topic the
// household shares, one person's devices for a private one.
public sealed class WebPushNotificationServiceTests
{
    private readonly Mock<IPushSubscriptionStore> _store = new();
    private readonly List<string> _reached = [];
    private readonly WebPushNotificationService _sut;

    public WebPushNotificationServiceTests()
    {
        _store.Setup(s => s.GetBySpaceAsync("default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                ("fran", new PushSubscriptionDto("https://push.example/fran-phone", "k", "a")),
                ("laura", new PushSubscriptionDto("https://push.example/laura-phone", "k", "a")),
                ("fran", new PushSubscriptionDto("https://push.example/fran-laptop", "k", "a"))
            ]);
        var sender = new Mock<IPushMessageSender>();
        sender.Setup(s => s.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string endpoint, string _, string _, string _, CancellationToken _) => _reached.Add(endpoint))
            .Returns(Task.CompletedTask);
        _sut = new WebPushNotificationService(_store.Object, sender.Object, NullLogger<WebPushNotificationService>.Instance);
    }

    [Fact]
    public async Task APersonsPush_ReachesTheirDevicesInTheSpaceAndNobodyElses()
    {
        await _sut.SendToPersonAsync("fran", "default", "Plum jam", "The agent has finished responding", "/default");

        _reached.ShouldBe(["https://push.example/fran-phone", "https://push.example/fran-laptop"]);
    }

    [Fact]
    public async Task ASpacesPush_ReachesEveryoneInIt()
    {
        await _sut.SendToSpaceAsync("default", "Plum jam", "The agent has finished responding", "/default");

        _reached.Count.ShouldBe(3);
    }
}