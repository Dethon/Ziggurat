using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public class TopicFilterChoiceTests
{
    public static TheoryData<TopicFilter> Filters => [TopicFilter.Mine, TopicFilter.Everyone, TopicFilter.Of("fran")];

    [Theory]
    [MemberData(nameof(Filters))]
    public void AFilter_ReadsBackAsItself(TopicFilter filter)
    {
        TopicFilterChoice.Parse(TopicFilterChoice.Format(filter)).ShouldBe(filter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("of:")]
    [InlineData("somebody")]
    public void AnythingElse_ReadsAsTheirOwn(string? value)
    {
        TopicFilterChoice.Parse(value).ShouldBe(TopicFilter.Mine);
    }
}