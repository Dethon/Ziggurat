using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A channel server asks the accounts service whose account the identity it saw belongs to, with the
// credential it already holds, and is answered the household, the person and the name they go by.
// Nobody else asks: a caller without one of the configured credentials is refused before anything
// is looked up.
public class IdentityResolutionTests
{
    private const string SignalRCredential = "signalr-credential";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = SignalRCredential,
                ["mcp-channel-telegram"] = "telegram-credential"
            }
        });

    [Theory]
    [InlineData("signalr")]
    [InlineData("voice")]
    public async Task AnAccountsOwnId_IsAnsweredWithItsHouseholdAndName(string channel)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, channel, fran.UserId, SignalRCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var account = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        account["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        account["userId"]!.GetValue<string>().ShouldBe(fran.UserId);
        account["displayName"]!.GetValue<string>().ShouldBe("Fran");
    }

    [Fact]
    public async Task AnyConfiguredCaller_MayAsk()
    {
        await using var server = await StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var response = await ResolveAsync(server, "signalr", laura, "telegram-credential");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // No Telegram id is linked to anyone yet, so every one is nobody's; the shape is the one a
    // linked id will be answered on.
    [Theory]
    [InlineData("signalr", "0123456789abcdef0123456789abcdef")]
    [InlineData("voice", "0123456789abcdef0123456789abcdef")]
    [InlineData("telegram", "123456789")]
    public async Task AnIdentityNoAccountHolds_Is404(string channel, string identity)
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, channel, identity, SignalRCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AChannelNobodyResolves_Is400()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "servicebus", fran.UserId, SignalRCredential);

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

        var response = await ResolveAsync(server, "signalr", fran.UserId, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // An unset credential refuses everything rather than opening the door: a forgotten variable
    // must not let anyone on the network ask who is who.
    [Fact]
    public async Task WithNoCallersConfigured_EveryoneIs401()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-signalr"] = "" }
        });
        var fran = await server.MintOperatorAsync("Fran");

        var response = await ResolveAsync(server, "signalr", fran.UserId, "");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> ResolveAsync(
        AccountsServer server, string channel, string identity, string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/internal/resolve")
        {
            Content = JsonContent.Create(new { channel, identity })
        };
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        }

        return server.Client().SendAsync(request);
    }
}