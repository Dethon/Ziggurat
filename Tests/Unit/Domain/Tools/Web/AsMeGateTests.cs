using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class AsMeGateTests
{
    private static readonly IdentityMap _identities = new(
        [new PersonIdentity { Name = "fran", Telegram = "FranTG" }],
        [IdentityMap.TelegramChannelId]);

    private readonly Mock<ILoginSites> _logins = new();

    private AsMeGate Gate => new(_identities, _logins.Object);

    private static ConversationContext Turn(string userId = "FranTG", MessageOriginKind? origin = null) =>
        new("jonas", "conv-1", userId, new ReplyTarget("telegram", "conv-1"), OriginKind: origin,
            Principal: new Principal("telegram", userId));

    private void LoggedInTo(bool loggedIn) =>
        _logins.Setup(l => l.HasLoginAsync("fran", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(loggedIn);

    [Fact]
    public async Task ASiteThePersonHasALoginFor_IsBrowsedAsThem()
    {
        LoggedInTo(true);

        var resolution = await Gate.ResolveAsync(Turn(), "https://shop.test/cuenta", CancellationToken.None);

        resolution.ShouldBeOfType<PersonResolution.Found>().Person.ShouldBe("fran");
    }

    // A page's injected text can name any site; the person's session reaches only the ones they
    // handed off, and on their own turn the way to another is a login they do themselves.
    [Fact]
    public async Task ASiteWithNoLogin_IsRefused_PointingAtTheHandoff()
    {
        LoggedInTo(false);

        var refused = (await Gate.ResolveAsync(Turn(), "https://mail.example/inbox", CancellationToken.None))
            .ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        refused.Error["message"]!.GetValue<string>().ShouldContain("mail.example");
        refused.Error["hint"]!.GetValue<string>().ShouldContain("web_login");
    }

    // Nobody is there to log in on a schedule's turn, so it reports instead of handing off.
    [Fact]
    public async Task ASiteWithNoLogin_OnAFire_IsRefused_AskingForAReport()
    {
        LoggedInTo(false);

        var refused = (await Gate.ResolveAsync(Turn(origin: MessageOriginKind.Schedule), "https://shop.test/",
                CancellationToken.None))
            .ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["hint"]!.GetValue<string>().ShouldNotContain("web_login");
        refused.Error["hint"]!.GetValue<string>().ShouldContain("report");
    }

    [Fact]
    public async Task SavedLoginsThatWillNotOpen_AreRefusedAsUnsupported()
    {
        _logins.Setup(l => l.HasLoginAsync("fran", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PersonStateUnreadableException("fran", "the configured key does not open them"));

        var refused = (await Gate.ResolveAsync(Turn(), "https://shop.test/", CancellationToken.None))
            .ShouldBeOfType<PersonResolution.Refused>();

        refused.Error["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task SomebodyNotInTheMap_IsRefusedBeforeAnyLoginIsLookedUp()
    {
        (await Gate.ResolveAsync(Turn("stranger"), "https://shop.test/", CancellationToken.None))
            .ShouldBeOfType<PersonResolution.Refused>();

        _logins.VerifyNoOtherCalls();
    }
}