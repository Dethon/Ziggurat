using Shouldly;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class TransientErrorFilterTests
{
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    public void IsTransientException_WithCancellationException_ReturnsTrue(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        TransientErrorFilter.IsTransientException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsTransientException_WithOtherException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Some error");

        TransientErrorFilter.IsTransientException(ex).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTransientErrorMessage_WithEmptyMessage_ReturnsTrue(string? message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("OperationCanceled")]
    [InlineData("TaskCanceled exception")]
    [InlineData("The operation was canceled.")]
    [InlineData("OPERATIONCANCELED")] // case insensitive
    public void IsTransientErrorMessage_WithTransientMessage_ReturnsTrue(string message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Connection reset by peer")]
    public void IsTransientErrorMessage_WithRealError_ReturnsFalse(string message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeFalse();
    }
}