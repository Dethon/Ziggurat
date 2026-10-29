using Domain.DTOs.Channel;
using Domain.Tools;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class IdentityMapTests
{
    // The shipped list: the two channels that know who is speaking today.
    private static readonly string[] _verified = [IdentityMap.TelegramChannelId, IdentityMap.VoiceChannelId];

    private static readonly IdentityMap _map = new(
    [
        new PersonIdentity { Name = "fran", Telegram = "FranTG", WebChat = "Fran", Voice = "fran" },
        new PersonIdentity { Name = "ana", Telegram = "ana_t", WebChat = "Ana" }
    ], _verified);

    private static ConversationContext From(string userId, string channelId = "telegram") =>
        new("jonas", "conv-1", userId, new ReplyTarget(channelId, "conv-1"));

    [Theory]
    [InlineData("FranTG")]
    [InlineData("frantg")]
    public void APersonsTelegramUsername_OnTelegram_ResolvesToThem(string userId)
    {
        _map.Resolve(From(userId)).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("fran");
    }

    [Fact]
    public void AnEnrolledVoice_OnVoice_ResolvesToThem()
    {
        _map.Resolve(From("fran", "voice")).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("fran");
    }

    [Fact]
    public void AnotherPersonsId_ResolvesToThem()
    {
        _map.Resolve(From("ana_t")).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("ana");
    }

    // A person's ids are matched only on the channel that issued them: a WebChat name that happens
    // to equal somebody's Telegram username, or their voice id, is not them on Telegram.
    [Theory]
    [InlineData("Fran", "telegram")]
    [InlineData("fran", "telegram")]
    [InlineData("FranTG", "voice")]
    public void AnIdFromAnotherChannel_IsNotThePerson(string userId, string channelId)
    {
        _map.Resolve(From(userId, channelId)).ShouldBeOfType<PersonResolution.Refused>();
    }

    // WebChat's user is whatever the page's picker sent, so until it is verified a WebChat turn is
    // nobody — refused by name, and told where to ask instead.
    [Fact]
    public void AWebChatTurn_WhileWebChatIsUnverified_IsRefusedSayingWhereToAsk()
    {
        var refused = _map.Resolve(From("Fran", "signalr")).ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        refused.Error["message"]!.GetValue<string>().ShouldContain("verify who is speaking");
        refused.Error["hint"]!.GetValue<string>().ShouldContain("Telegram");
        refused.Error["hint"]!.GetValue<string>().ShouldContain("voice");
    }

    [Fact]
    public void AWebChatTurn_OnceWebChatIsVerified_ResolvesByTheWebChatName()
    {
        var map = new IdentityMap(
            [new PersonIdentity { Name = "fran", Telegram = "FranTG", WebChat = "Fran" }],
            [.. _verified, IdentityMap.WebChatChannelId]);

        map.Resolve(From("Fran", "signalr")).ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("fran");
        map.Resolve(From("FranTG", "signalr")).ShouldBeOfType<PersonResolution.Refused>();
    }

    [Fact]
    public void AChannelNobodyListed_IsRefused()
    {
        _map.Resolve(From("FranTG", "sugary")).ShouldBeOfType<PersonResolution.Refused>()
            .Error["message"]!.GetValue<string>().ShouldContain("verify who is speaking");
    }

    [Fact]
    public void WebChatIsVerified_OnlyWhenListed()
    {
        _map.IsVerified(IdentityMap.WebChatChannelId).ShouldBeFalse();
        _map.IsVerified(IdentityMap.TelegramChannelId).ShouldBeTrue();
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
        var map = new IdentityMap([new PersonIdentity { Name = "fran", Voice = "household" }], _verified);

        var refused = map.Resolve(From("household", "voice")).ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["message"]!.GetValue<string>().ShouldContain("recognise");
        refused.Error["hint"]!.GetValue<string>().ShouldNotContain("WebChat");
    }

    [Fact]
    public void AnEmptyMap_IsTheFeatureOff()
    {
        var map = new IdentityMap([], _verified);

        map.IsEnabled.ShouldBeFalse();
        var refused = map.Resolve(From("fran")).ShouldBeOfType<PersonResolution.Refused>();
        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // The compose file ships the map's slots as empty placeholders; an unfilled one is no person.
    [Fact]
    public void APlaceholderPersonWithNoName_IsLeftOut()
    {
        var map = new IdentityMap([new PersonIdentity { Name = "", Telegram = "", WebChat = "", Voice = "" }], _verified);

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