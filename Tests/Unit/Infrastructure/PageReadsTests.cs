using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PageReadsTests
{
    [Fact]
    public async Task APageThatAnswers_IsReadAsItIs()
    {
        var page = new Mock<IPage>();
        page.Setup(p => p.ContentAsync()).ReturnsAsync("<html></html>");

        (await PageReads.ContentAsync(page.Object, new FakeTimeProvider())).ShouldBe("<html></html>");
    }

    [Fact]
    public async Task APageThatDoesNotAnswerWithinTheBound_IsUnresponsive()
    {
        // page.content() has no timeout of its own: a page whose script holds its thread kept one
        // browse waiting on it for 35 minutes.
        var time = new FakeTimeProvider();
        var page = new Mock<IPage>();
        page.SetupGet(p => p.Url).Returns("https://hung.test/");
        page.Setup(p => p.ContentAsync()).Returns(new TaskCompletionSource<string>().Task);

        var read = PageReads.ContentAsync(page.Object, time);
        time.Advance(PageReads.Deadline - TimeSpan.FromSeconds(1));
        read.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        (await Should.ThrowAsync<PageUnresponsiveException>(read.WaitAsync(TimeSpan.FromSeconds(10))))
            .Url.ShouldBe("https://hung.test/");
    }
}