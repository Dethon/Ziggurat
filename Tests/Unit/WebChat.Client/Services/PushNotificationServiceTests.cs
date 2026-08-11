using Microsoft.JSInterop;
using Moq;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class PushNotificationServiceTests
{
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly Mock<IChatLiveConnection> _mockLiveConnection;
    private readonly PushNotificationService _sut;

    public PushNotificationServiceTests()
    {
        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockLiveConnection = new Mock<IChatLiveConnection>();
        _sut = new PushNotificationService(_mockJsRuntime.Object, _mockLiveConnection.Object);
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionDenied_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("denied"));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenTheCallCouldNotBeMade_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult>(new PushSubscriptionResult("https://endpoint", "key", "auth")));
        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenSubscribeReturnsNull_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult>((PushSubscriptionResult?)null!));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ResubscribeAsync_WhenSubscriptionExists_ResendsItOverTheHub()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult?>(new PushSubscriptionResult("https://endpoint", "key", "auth")));

        await _sut.ResubscribeAsync();

        _mockJsRuntime.Verify(js => js.InvokeAsync<PushSubscriptionResult?>(
            "pushNotifications.getSubscription", It.IsAny<object[]>()), Times.Once);
        _mockLiveConnection.Verify(
            c => c.InvokeAsync("SubscribePush", It.IsAny<object?[]>()), Times.Once);
    }

    [Fact]
    public async Task ResubscribeAsync_WhenNoSubscription_MakesNoHubCall()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult?>((PushSubscriptionResult?)null));

        await _sut.ResubscribeAsync();

        _mockLiveConnection.Verify(
            c => c.InvokeAsync(It.IsAny<string>(), It.IsAny<object?[]>()), Times.Never);
    }
}