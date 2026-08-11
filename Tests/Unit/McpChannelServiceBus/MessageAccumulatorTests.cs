using McpChannelServiceBus.Services;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class MessageAccumulatorTests
{
    private readonly MessageAccumulator _sut = new();

    [Fact]
    public void Flush_AccumulatesAppendsAndReturnsAll()
    {
        _sut.Flush("conv-1").ShouldBeNull();

        _sut.Append("conv-1", "hello ");
        _sut.Flush("conv-1").ShouldBe("hello ");

        _sut.Append("conv-1", "hello ");
        _sut.Append("conv-1", "world");
        _sut.Flush("conv-1").ShouldBe("hello world");
    }

    [Fact]
    public void Flush_SeparateConversations_IndependentBuffers()
    {
        _sut.Append("conv-1", "first");
        _sut.Append("conv-2", "second");

        _sut.Flush("conv-1").ShouldBe("first");
        _sut.Flush("conv-2").ShouldBe("second");
    }
}