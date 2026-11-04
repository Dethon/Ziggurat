using Domain.Contracts;
using Domain.DTOs.Channel;
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
        _store.Setup(s => s.GetBySpaceAsync("home", "default", It.IsAny<CancellationToken>()))
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
        await _sut.SendToPersonAsync(new Account("home", "fran"), "default", "Plum jam", "The agent has finished responding", "/default");

        _reached.ShouldBe(["https://push.example/fran-phone", "https://push.example/fran-laptop"]);
    }

    [Fact]
    public async Task ASpacesPush_ReachesEveryoneInIt()
    {
        await _sut.SendToSpaceAsync("home", "default", "Plum jam", "The agent has finished responding", "/default");

        _reached.Count.ShouldBe(3);
    }

    // A phone is subscribed by a signed-in session, and hears nothing once that session ends — signed
    // out from the account page (a lost phone), or its person disabled: it is dropped. One whose
    // session could not be asked about just now is told nothing either, and kept.
    [Fact]
    public async Task APhoneWhoseSessionEnded_IsDropped_AndOneThatCouldNotBeAskedIsSkipped()
    {
        _store.Setup(s => s.GetBySpaceAsync("home", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                ("fran", new PushSubscriptionDto("https://push.example/fran-phone", "k", "a") { SessionId = "lost" }),
                ("fran", new PushSubscriptionDto("https://push.example/fran-laptop", "k", "a") { SessionId = "live" }),
                ("laura", new PushSubscriptionDto("https://push.example/laura-phone", "k", "a") { SessionId = "unknown" })
            ]);
        var sessions = new Mock<ISessionAccounts>();
        sessions.Setup(s => s.SessionAsync("fran", "lost", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedAccount?)null);
        sessions.Setup(s => s.SessionAsync("fran", "live", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAccount(new Account("home", "fran"), "Fran", "assertion"));
        sessions.Setup(s => s.SessionAsync("laura", "unknown", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("accounts is down"));
        var sender = new Mock<IPushMessageSender>();
        sender.Setup(s => s.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string endpoint, string _, string _, string _, CancellationToken _) => _reached.Add(endpoint))
            .Returns(Task.CompletedTask);
        var sut = new WebPushNotificationService(
            _store.Object, sender.Object, NullLogger<WebPushNotificationService>.Instance, sessions.Object);

        await sut.SendToSpaceAsync("home", "default", "Plum jam", "The agent has finished responding", "/default");

        _reached.ShouldBe(["https://push.example/fran-laptop"]);
        _store.Verify(s => s.RemoveByEndpointAsync("https://push.example/fran-phone", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.RemoveByEndpointAsync("https://push.example/laura-phone", It.IsAny<CancellationToken>()), Times.Never);
    }
}