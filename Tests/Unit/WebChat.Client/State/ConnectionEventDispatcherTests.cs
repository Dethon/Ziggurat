using Moq;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ConnectionEventDispatcherTests
{
    private readonly Mock<IDispatcher> _mockDispatcher;
    private readonly ConnectionEventDispatcher _sut;

    public ConnectionEventDispatcherTests()
    {
        _mockDispatcher = new Mock<IDispatcher>();
        _sut = new ConnectionEventDispatcher(_mockDispatcher.Object);
    }

    public static TheoryData<string, Type> HandlerDispatchData => new()
    {
        { nameof(ConnectionEventDispatcher.HandleConnecting), typeof(ConnectionConnecting) },
        { nameof(ConnectionEventDispatcher.HandleConnected), typeof(ConnectionConnected) },
        { nameof(ConnectionEventDispatcher.HandleReconnecting), typeof(ConnectionReconnecting) },
        { nameof(ConnectionEventDispatcher.HandleReconnected), typeof(ConnectionReconnected) },
    };

    [Theory]
    [MemberData(nameof(HandlerDispatchData))]
    public void Handle_DispatchesCorrectAction(string handlerName, Type expectedActionType)
    {
        var handler = typeof(ConnectionEventDispatcher).GetMethod(handlerName)!;

        handler.Invoke(_sut, null);

        _mockDispatcher.Invocations.Count.ShouldBe(1);
        _mockDispatcher.Invocations[0].Arguments[0].ShouldNotBeNull().GetType().ShouldBe(expectedActionType);
    }

    [Fact]
    public void HandleClosed_DispatchesConnectionClosedWithCorrectError()
    {
        var exception = new InvalidOperationException("Server shutdown");

        _sut.HandleClosed(exception);
        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ConnectionClosed>(action => action.Error == "Server shutdown")),
            Times.Once);

        _mockDispatcher.Invocations.Clear();

        _sut.HandleClosed(null);
        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ConnectionClosed>(action => action.Error == null)),
            Times.Once);
    }
}