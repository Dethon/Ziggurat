using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

// A notification's tap opens the app at its space's page with the conversation in the query; the
// app reads it once and then takes it off the address, so a reload later does not drag the person
// back to it.
public sealed class LaunchLinkTests
{
    [Fact]
    public void AnAddressNamingAConversation_IsReadAsIt()
    {
        var link = new LaunchLink(new FakeNavigationManager("http://localhost/kitchen?agent=jonas&topic=t%2B1"));

        link.Topic.ShouldBe(new LaunchTopic("jonas", "t+1"));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/kitchen?topic=t1")]
    [InlineData("http://localhost/kitchen?agent=jonas")]
    [InlineData("http://localhost/kitchen?agent=&topic=t1")]
    public void AnAddressNamingNoWholeConversation_NamesNone(string uri) =>
        new LaunchLink(new FakeNavigationManager(uri)).Topic.ShouldBeNull();

    [Fact]
    public void Spending_ForgetsItAndTakesItOffTheAddress()
    {
        var navigation = new FakeNavigationManager("http://localhost/kitchen?agent=jonas&topic=t1");
        var link = new LaunchLink(navigation);

        link.Spend();

        link.Topic.ShouldBeNull();
        navigation.NavigatedTo.ShouldBe(["/kitchen"]);
    }

    [Fact]
    public void SpendingAnAddressThatNamedNone_LeavesTheAddressAlone()
    {
        var navigation = new FakeNavigationManager("http://localhost/kitchen");

        new LaunchLink(navigation).Spend();

        navigation.NavigatedTo.ShouldBeEmpty();
    }
}