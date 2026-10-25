using Domain.DTOs.Channel;
using Domain.Tools;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class IdentityMapTests
{
    private static readonly IdentityMap _map = new(
    [
        new PersonIdentity { Name = "fran", Telegram = "FranTG", WebChat = "Fran", Voice = "fran" },
        new PersonIdentity { Name = "ana", Telegram = "ana_t", WebChat = "Ana" }
    ]);

    private static ConversationContext From(string userId) =>
        new("jonas", "conv-1", userId, new ReplyTarget("telegram", "conv-1"));

    [Theory]
    [InlineData("FranTG")]
    [InlineData("frantg")]
    [InlineData("Fran")]
    [InlineData("fran")]
    public void EveryIdOfAPerson_ResolvesToThatPerson(string userId)
    {
        _map.Resolve(From(userId)).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("fran");
    }

    [Fact]
    public void AnotherPersonsId_ResolvesToThem()
    {
        _map.Resolve(From("ana_t")).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("ana");
    }

    [Fact]
    public void AnIdNobodyIsMappedTo_IsRefusedNamingIt()
    {
        var refused = _map.Resolve(From("stranger")).ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        refused.Error["message"]!.GetValue<string>().ShouldContain("stranger");
    }

    // The voice channel's fallback when no enrolled speaker matched: whoever is talking, nobody
    // in particular — even on a deployment that mapped the word by mistake.
    [Fact]
    public void TheVoiceHouseholdFallback_IsNoPerson()
    {
        var map = new IdentityMap([new PersonIdentity { Name = "fran", Voice = "household" }]);

        var refused = map.Resolve(From("household")).ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["message"]!.GetValue<string>().ShouldContain("recognise");
    }

    [Fact]
    public void AnEmptyMap_IsTheFeatureOff()
    {
        var map = new IdentityMap([]);

        map.IsEnabled.ShouldBeFalse();
        var refused = map.Resolve(From("fran")).ShouldBeOfType<PersonResolution.Refused>();
        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // The compose file ships the map's slots as empty placeholders; an unfilled one is no person.
    [Fact]
    public void APlaceholderPersonWithNoName_IsLeftOut()
    {
        var map = new IdentityMap([new PersonIdentity { Name = "", Telegram = "", WebChat = "", Voice = "" }]);

        map.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void NoContext_IsRefused()
    {
        _map.Resolve(null).ShouldBeOfType<PersonResolution.Refused>();
    }

    [Fact]
    public void APersonsWebChatName_IsFoundByTheirName()
    {
        _map.WebChatNameOf("fran").ShouldBe("Fran");
        _map.WebChatNameOf("nobody").ShouldBeNull();
    }
}