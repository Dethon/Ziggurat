using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Security;
using Domain.Security.Signing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A channel server asks the accounts service whose account the identity it saw belongs to, with the
// credential it already holds, and is answered the household, the person and the name they go by.
// Nobody else asks: a caller without one of the configured credentials is refused before anything
// is looked up, and one asking about another channel than its own is refused too (InternalCallersTests).
public class IdentityResolutionTests
{
    private const string SignalRCredential = "signalr-credential";
    private const string TelegramCredential = "telegram-credential";
    private const string VoiceCredential = "voice-credential";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = SignalRCredential,
                ["mcp-channel-telegram"] = TelegramCredential,
                ["mcp-channel-voice"] = VoiceCredential
            }
        });

    // A voice profile is named after the account it belongs to; the chat hub's person is a session's
    // (SessionRefreshTests), never an id resolved here.
    [Fact]
    public async Task AnAccountsOwnId_IsAnsweredWithItsHouseholdAndName()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "voice", fran.UserId, VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var account = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        account["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        account["userId"]!.GetValue<string>().ShouldBe(fran.UserId);
        account["displayName"]!.GetValue<string>().ShouldBe("Fran");
    }

    // The answer vouches for itself: a channel server carries the assertion on the turn it starts,
    // and the agent believes the person it names because this service signed it for that channel.
    [Fact]
    public async Task AResolvedIdentity_IsAnsweredWithAnAssertionOfItsPersonOnThatChannel()
    {
        const string channel = "voice";
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, channel, fran.UserId, VoiceCredential);

        var account = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var assertion = await server.VerifyAssertionAsync(account["assertion"]!.GetValue<string>());
        assertion.IsVerified.ShouldBeTrue(assertion.Refusal.ToString());
        assertion.Claims.Subject.ShouldBe(fran.UserId);
        assertion.Claims.TenantId.ShouldBe(fran.TenantId);
        assertion.Claims.ChannelId.ShouldBe(channel);
        assertion.Claims.Roles.ShouldBe(["owner", "operator"]);
    }

    // Somebody at a satellite whom no profile matched is the household's guest, and that too is
    // vouched for: a signed guest, never the absence of a signature, which is nobody at all.
    [Fact]
    public async Task TheVoiceGuest_IsAnsweredWithASignedGuestOfTheHomeHousehold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await GuestAsync(server, "voice", VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var guest = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        guest["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        var assertion = await server.VerifyAssertionAsync(guest["assertion"]!.GetValue<string>());
        assertion.IsVerified.ShouldBeTrue(assertion.Refusal.ToString());
        assertion.Claims.IsGuest.ShouldBeTrue();
        assertion.Claims.Account.ShouldBeNull();
        assertion.Claims.TenantId.ShouldBe(fran.TenantId);
        assertion.Claims.ChannelId.ShouldBe("voice");
        assertion.Claims.Roles.ShouldBeEmpty();
    }

    // Only a satellite has a household guest: anyone else a channel cannot name is nobody's turn.
    [Theory]
    [InlineData("signalr")]
    [InlineData("telegram")]
    [InlineData("servicebus")]
    public async Task AGuestOnAnotherChannel_Is400(string channel)
    {
        await using var server = await StartAsync();

        var response = await GuestAsync(server, channel, VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-credential")]
    public async Task AGuestAskedWithoutAConfiguredCredential_Is401(string? credential)
    {
        await using var server = await StartAsync();

        var response = await GuestAsync(server, "voice", credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The keys are no secret and their verifiers hold no caller credential: the proxy is what keeps
    // them on the compose network (DeploymentConformanceTests).
    [Fact]
    public async Task TheJwks_IsServedWithoutACredential()
    {
        await using var server = await StartAsync();

        var response = await server.Client().GetAsync("/accounts/internal/jwks");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonWebKeySet.Parse(await response.Content.ReadAsStringAsync()).Keys.ShouldNotBeEmpty();
    }

    // A Telegram id nobody redeemed a code from is nobody's (TelegramLinkingTests links one).
    [Theory]
    [InlineData("voice", "0123456789abcdef0123456789abcdef", VoiceCredential)]
    [InlineData("telegram", "123456789", TelegramCredential)]
    public async Task AnIdentityNoAccountHolds_Is404(string channel, string identity, string credential)
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, channel, identity, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AChannelNobodyResolves_Is400()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "servicebus", fran.UserId, VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-credential")]
    [InlineData("")]
    public async Task ACallerWithoutAConfiguredCredential_Is401(string? credential)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "voice", fran.UserId, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // An unset credential refuses everything rather than opening the door: a forgotten variable
    // must not let anyone on the network ask who is who.
    [Fact]
    public async Task WithNoCallersConfigured_EveryoneIs401()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-voice"] = "" }
        });
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "voice", fran.UserId, "");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> GuestAsync(AccountsServer server, string channel, string? credential) =>
        PostAsync(server, "/accounts/internal/guest", new { channel }, credential);

    private static Task<HttpResponseMessage> ResolveAsync(
        AccountsServer server, string channel, string identity, string? credential) =>
        PostAsync(server, "/accounts/internal/resolve", new { channel, identity }, credential);

    private static Task<HttpResponseMessage> PostAsync<T>(AccountsServer server, string path, T body, string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        }

        return server.Client().SendAsync(request);
    }
}