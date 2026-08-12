using McpChannelTelegram.Services;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

public class MessageAccumulatorTests
{
    private readonly MessageAccumulator _sut = new();

    [Theory]
    [InlineData("hello", null, "hello")]
    [InlineData("hello ", "world", "hello world")]
    public void Flush_AppendedChunks_ConcatenatesCorrectly(string first, string? second, string expected)
    {
        _sut.Append("conv-1", first);
        if (second is not null)
        {
            _sut.Append("conv-1", second);
        }

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(1);
        result[0].ShouldBe(expected);
    }

    [Fact]
    public void Flush_RemovesBuffer_SecondFlushReturnsEmpty()
    {
        _sut.Append("conv-1", "hello");
        _sut.Flush("conv-1");

        _sut.Flush("conv-1").ShouldBeEmpty();
    }

    [Fact]
    public void Flush_SeparateConversations_IndependentBuffers()
    {
        _sut.Append("conv-1", "first");
        _sut.Append("conv-2", "second");

        _sut.Flush("conv-1")[0].ShouldBe("first");
        _sut.Flush("conv-2")[0].ShouldBe("second");
    }

    [Fact]
    public void Flush_ExactlyAtLimit_ReturnsSingleChunk()
    {
        var text = new string('a', 4096);
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(1);
        result[0].Length.ShouldBe(4096);
    }

    [Fact]
    public void Flush_LongTextNoNewlines_SplitsAtLimit()
    {
        var text = new string('a', 8192);
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(2);
        result[0].Length.ShouldBe(4096);
        result[1].Length.ShouldBe(4096);
    }
}