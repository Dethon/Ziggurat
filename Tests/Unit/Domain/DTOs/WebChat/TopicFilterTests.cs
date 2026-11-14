using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

// Which of the topics a person sees their list shows. A filter only narrows what they may see: it is
// asked beside TopicViewer.Sees, never instead of it.
public class TopicFilterTests
{
    private const string Home = "home";
    private const string Olga = "olga";
    private const string Fran = "fran";
    private const string Laura = "laura";

    private static readonly TopicViewer SeesAll = new(Olga, Home, SeesAll: true);

    private static TopicMetadata Topic(string? owner, bool shared = false, string? tenant = Home) =>
        new($"t-{owner}-{shared}", 1, 1, "jonas", "a topic", DateTimeOffset.UnixEpoch, null,
            OwnerId: owner, TenantId: tenant, Shared: shared);

    // "Mine" is what the person would see without being granted everyone's: their own, what the
    // household shares, and what belongs to nobody.
    [Theory]
    [InlineData(Olga, false, true)]
    [InlineData(Fran, true, true)]
    [InlineData(null, false, true)]
    [InlineData(Fran, false, false)]
    public void Mine_ListsWhatThePersonWouldSeeWithoutSeeingAll(string? owner, bool shared, bool listed)
    {
        SeesAll.Lists(Topic(owner, shared), TopicFilter.Mine).ShouldBe(listed);
    }

    [Theory]
    [InlineData(Olga, false)]
    [InlineData(Fran, true)]
    [InlineData(null, false)]
    [InlineData(Fran, false)]
    public void Everyone_ListsEveryTopicThePersonSees(string? owner, bool shared)
    {
        SeesAll.Lists(Topic(owner, shared), TopicFilter.Everyone).ShouldBeTrue();
    }

    // One person's: theirs, shared or not, and nobody else's — not the reader's own, not nobody's.
    [Theory]
    [InlineData(Fran, false, true)]
    [InlineData(Fran, true, true)]
    [InlineData(Laura, false, false)]
    [InlineData(Olga, false, false)]
    [InlineData(null, false, false)]
    public void APerson_ListsThatPersonsTopicsAlone(string? owner, bool shared, bool listed)
    {
        SeesAll.Lists(Topic(owner, shared), TopicFilter.Of(Fran)).ShouldBe(listed);
    }

    // A filter can never show what the rule hides: a person's private topic stays hidden from a member
    // who does not see all, filtered by that person or not, and another household's from everyone.
    [Fact]
    public void AFilter_NeverListsWhatTheViewerMayNotSee()
    {
        var laura = new TopicViewer(Laura, Home);

        laura.Lists(Topic(Fran), TopicFilter.Of(Fran)).ShouldBeFalse();
        laura.Lists(Topic(Fran), TopicFilter.Everyone).ShouldBeFalse();
        SeesAll.Lists(Topic(Fran, tenant: "elsewhere"), TopicFilter.Of(Fran)).ShouldBeFalse();
    }

    // A client from before filters asks for nothing and is answered everything it sees.
    [Fact]
    public void NoFilter_IsEveryone()
    {
        SeesAll.Lists(Topic(Fran), null).ShouldBeTrue();
    }
}