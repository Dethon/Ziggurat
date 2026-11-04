using System.Net;
using System.Net.Http.Json;
using Accounts.Settings;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's keys through the admin API (seam 1 of Tenants 4c): each key is put to its provider
// before it is saved — a stub here, never a real one — a key the provider refuses is not saved and
// the owner is told why, one saved while the provider could not be asked stands unchecked with a
// warning, and no answer ever carries a saved key, only its state and its last few characters.
public sealed class KeysAdminTests : IAsyncLifetime
{
    private const string WorkingKey = "sk-or-v1-0123456789abcdef0123456789abcdef-work";
    private const string AgentCredential = "agent-credential";

    private FakeKeyProviders _providers = null!;

    public async Task InitializeAsync() => _providers = await FakeKeyProviders.StartAsync();

    public async Task DisposeAsync() => await _providers.DisposeAsync();

    [Fact]
    public async Task AKeyItsProviderAccepts_IsSaved_AndShownAsWorkingByItsLastCharactersAlone()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);

        var saved = await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadAsStringAsync()).ShouldNotContain(WorkingKey);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdKeys>>())!;
        answer.Value.OpenRouter.ShouldBe(new KeyStatus(KeyState.Working, "work", server.Clock.GetUtcNow()));
        answer.Value.NeedsOpenRouterKey.ShouldBeFalse();
        _providers.Asked.ShouldBe([WorkingKey]);
        (await KeysSectionAsync(server, household)).OpenRouter.ShouldBe(new OpenRouterKey(WorkingKey));
        var read = await admin.GetAsync(AdminProtocol.Keys);
        (await read.Content.ReadAsStringAsync()).ShouldNotContain(WorkingKey);
        (await read.Content.ReadFromJsonAsync<HouseholdKeys>())!.OpenRouter!.State.ShouldBe(KeyState.Working);
    }

    [Fact]
    public async Task AKeyItsProviderRejects_IsNotSaved_AndTheOwnerIsToldWhy()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message
            .ShouldBe("The OpenRouter key was not saved: OpenRouter refused it (401).");
        (await KeysSectionAsync(server, household)).OpenRouter.ShouldBeNull();
        var keys = (await (await admin.GetAsync(AdminProtocol.Keys)).Content.ReadFromJsonAsync<HouseholdKeys>())!;
        keys.OpenRouter.ShouldBeNull();
        keys.NeedsOpenRouterKey.ShouldBeTrue();
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    // A provider outage does not keep the owner from saving: the key stands, said to be unchecked.
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "OpenRouter answered 503")]
    [InlineData(HttpStatusCode.TooManyRequests, "OpenRouter answered 429")]
    [InlineData(null, "OpenRouter did not answer in time")]
    public async Task AKeySavedWhileItsProviderIsUnreachable_IsUnchecked_WithAWarning(HttpStatusCode? answer, string why)
    {
        await using var server = await StartAsync(
            settings => settings with { KeyProbes = settings.KeyProbes with { Timeout = TimeSpan.FromSeconds(1) } });
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        if (answer is { } status)
        {
            _providers.Answer(WorkingKey, status);
        }
        else
        {
            _providers.Stall(WorkingKey);
        }

        var saved = await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answered = (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdKeys>>())!;
        answered.Value.OpenRouter.ShouldBe(new KeyStatus(KeyState.Unchecked, "work", server.Clock.GetUtcNow(), why));
        answered.Warning.ShouldNotBeNull().ShouldContain("could not be asked");
        answered.Warning.ShouldContain(why);
        (await KeysSectionAsync(server, household)).OpenRouter.ShouldBe(new OpenRouterKey(WorkingKey));
    }

    [Fact]
    public async Task IdealistasPair_IsProbedByItsTokenExchange_AndShownByItsApiKeyAlone()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var pair = new IdealistaKeys("idealista-api-key-0123456789-apik", "idealista-secret-9876543210-secr");

        (await admin.PutAsync(AdminProtocol.IdealistaKeys, pair)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _providers.Accept(pair.ApiKey);
        var saved = await admin.PutAsync(AdminProtocol.IdealistaKeys, pair);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await saved.Content.ReadAsStringAsync();
        body.ShouldNotContain(pair.ApiKey);
        body.ShouldNotContain(pair.Secret);
        body.ShouldNotContain("secr");
        (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdKeys>>())!.Value.Idealista
            .ShouldBe(new KeyStatus(KeyState.Working, "apik", server.Clock.GetUtcNow()));
        (await KeysSectionAsync(server, household)).Idealista.ShouldBe(pair);
    }

    // A bot is bound to one of the household's agents; a household that saved none has the two
    // shipped templates, jonas and sugary.
    [Fact]
    public async Task ABotsToken_IsProbedByItsGetMe_AndBoundToOneOfTheHouseholdsAgents()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        const string token = "123456789:AAH-bot-token-0123456789abcdefghij";

        var notABot = await admin.PutAsync(AdminProtocol.TelegramBot("jonas"), new TelegramBotKey("https://example.com/x"));
        (await notABot.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("not a Telegram bot token");
        (await admin.PutAsync(AdminProtocol.TelegramBot("jonas"), new TelegramBotKey(token))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _providers.Accept(token);
        (await admin.PutAsync(AdminProtocol.TelegramBot("nobody"), new TelegramBotKey(token))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var saved = await admin.PutAsync(AdminProtocol.TelegramBot("jonas"), new TelegramBotKey(token));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadAsStringAsync()).ShouldNotContain(token);
        (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdKeys>>())!.Value.TelegramBots
            .ShouldBe([new TelegramBotStatus("jonas", new KeyStatus(KeyState.Working, "ghij", server.Clock.GetUtcNow()))]);
        (await KeysSectionAsync(server, household)).TelegramBots["jonas"].ShouldBe(new TelegramBotKey(token));
        _providers.Asked.ShouldNotContain("https://example.com/x");
    }

    // Setting one key keeps every other the household holds, sealed as they were.
    [Fact]
    public async Task SettingOneKey_KeepsTheOthers()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var pair = new IdealistaKeys("idealista-api-key-0123456789-apik", "idealista-secret-9876543210-secr");
        _providers.Accept(pair.ApiKey);
        _providers.Accept(WorkingKey);

        await admin.PutAsync(AdminProtocol.IdealistaKeys, pair);
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        var keys = await KeysSectionAsync(server, household);
        keys.Idealista.ShouldBe(pair);
        keys.OpenRouter.ShouldBe(new OpenRouterKey(WorkingKey));
    }

    // The operator's command asks no provider, so a key it saved over a checked one is not that one's.
    [Fact]
    public async Task AKeyReplacedByTheOperatorsCommand_ShowsAsUnchecked()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        // Saved, though the readers could not be told: this server has no Redis.
        await server.RunAsync(server.Settings,
            """{"openRouter":{"apiKey":"sk-or-v1-from-the-operators-command-0000-oper"}}""",
            "set-household-config", "--household", household, "--section", HouseholdSections.Keys, "--file", "-");

        (await (await admin.GetAsync(AdminProtocol.Keys)).Content.ReadFromJsonAsync<HouseholdKeys>())!.OpenRouter
            .ShouldBe(new KeyStatus(KeyState.Unchecked, "oper", null));
    }

    // Until it has a key of its own a household's agents refuse its turns; the home's run on the
    // deployment's key whatever it holds.
    [Fact]
    public async Task OnlyAHouseholdWithoutAKeyOfItsOwn_NeedsOne()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var home = (await (await server.Admin(await server.MintSessionAsync(fran.UserId)).GetAsync(AdminProtocol.Keys))
            .Content.ReadFromJsonAsync<HouseholdKeys>())!;
        var casa = (await (await server.Admin(await server.MintSessionAsync(ana)).GetAsync(AdminProtocol.Keys))
            .Content.ReadFromJsonAsync<HouseholdKeys>())!;

        home.PaysWithDeploymentKey.ShouldBeTrue();
        home.OpenRouter.ShouldBeNull();
        home.NeedsOpenRouterKey.ShouldBeFalse();
        casa.PaysWithDeploymentKey.ShouldBeFalse();
        casa.NeedsOpenRouterKey.ShouldBeTrue();
    }

    [Fact]
    public async Task AMember_IsRefusedTheKeys()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));
        _providers.Accept(WorkingKey);

        (await laura.GetAsync(AdminProtocol.Keys)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _providers.Asked.ShouldBeEmpty();
    }

    // Who set or replaced which key, and when — never any of it.
    [Fact]
    public async Task AKeyChange_IsRecorded_WithoutTheKey()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        const string replacement = "sk-or-v1-the-replacement-key-0123456789-repl";
        _providers.Accept(WorkingKey);
        _providers.Accept(replacement);

        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(replacement));

        var changes = await ChangesAsync(admin);
        changes.Select(c => c.Summary).ShouldBe(["Replaced the OpenRouter key", "Set the OpenRouter key"]);
        changes.ShouldAllBe(c => c.Route == $"PUT {AdminProtocol.OpenRouterKey}");
    }

    // A stack with no master key keeps no secrets: a key is refused with why, as the deployment's
    // unavailability (503, as a read is) rather than the request's fault, and never a 500.
    [Fact]
    public async Task OnAStackWithNoMasterKey_AKeyIsRefused_WithWhy()
    {
        await using var server = await StartAsync(settings => settings with { HouseholdConfig = new HouseholdConfigSettings() });
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);

        var refused = await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("cannot be opened");
        (await admin.GetAsync(AdminProtocol.Keys)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The agent says OpenRouter refused the household's key in a turn: it shows as rejected at once,
    // and OpenRouter is asked again, whose answer stands — a key it refuses stays rejected.
    [Fact]
    public async Task AKeyTheAgentSaysFailedInATurn_ShowsAsRejected_AndIsAskedAboutAgain()
    {
        await using var server = await StartAsync(AdmittingTheAgent);
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));
        _providers.Answer(WorkingKey, HttpStatusCode.Unauthorized);
        server.Clock.Advance(TimeSpan.FromHours(1));

        await AgentsReader(server).OpenRouterKeyRefusedAsync(household, CancellationToken.None);

        var keys = (await (await admin.GetAsync(AdminProtocol.Keys)).Content.ReadFromJsonAsync<HouseholdKeys>())!;
        keys.OpenRouter.ShouldBe(new KeyStatus(KeyState.Rejected, "work", server.Clock.GetUtcNow(), "OpenRouter refused it (401)"));
        _providers.Asked.ShouldBe([WorkingKey, WorkingKey]);
    }

    // A refusal OpenRouter no longer gives — a key the owner topped up, a hiccup — is not left standing.
    [Fact]
    public async Task AKeyTheAgentSaysFailed_ThatOpenRouterTakesWhenAskedAgain_ShowsAsWorking()
    {
        await using var server = await StartAsync(AdmittingTheAgent);
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));

        await AgentsReader(server).OpenRouterKeyRefusedAsync(household, CancellationToken.None);

        (await (await admin.GetAsync(AdminProtocol.Keys)).Content.ReadFromJsonAsync<HouseholdKeys>())!.OpenRouter!.State
            .ShouldBe(KeyState.Working);
    }

    // Only the agent says a key failed in use, with its own token beside its credential: anyone else
    // asking would have every household's keys asked about at will.
    [Fact]
    public async Task OnlyTheAgent_SaysAKeyFailedInUse()
    {
        await using var server = await StartAsync(settings => settings with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential, ["mcp-sugary"] = "sugary-credential" }
        });
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);
        await admin.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(WorkingKey));
        _providers.Answer(WorkingKey, HttpStatusCode.Unauthorized);

        await Should.ThrowAsync<HttpRequestException>(() => Reader(server, "sugary-credential", withAgentToken: true)
            .OpenRouterKeyRefusedAsync(household, CancellationToken.None));
        await Should.ThrowAsync<HouseholdConfigRefusedException>(() => Reader(server, AgentCredential, withAgentToken: false)
            .OpenRouterKeyRefusedAsync(household, CancellationToken.None));

        (await (await admin.GetAsync(AdminProtocol.Keys)).Content.ReadFromJsonAsync<HouseholdKeys>())!.OpenRouter!.State
            .ShouldBe(KeyState.Working);
    }

    private static AccountsSettings AdmittingTheAgent(AccountsSettings settings) =>
        settings with { Callers = new Dictionary<string, string> { ["agent"] = AgentCredential } };

    private static HouseholdConfigurationClient AgentsReader(AccountsServer server) =>
        Reader(server, AgentCredential, withAgentToken: true);

    private static HouseholdConfigurationClient Reader(AccountsServer server, string credential, bool withAgentToken) =>
        new(server.Client(), credential,
            withAgentToken ? HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens) : () => "not-a-token",
            redis: null, TimeProvider.System, TimeSpan.FromMinutes(1));

    // A household's own Lemonade box is asked for its models with the key before it is saved, shown
    // by its address and its key's last characters, never the key.
    [Fact]
    public async Task ALemonadeBoxThatAnswersItsKey_IsSaved_AndShownByItsAddress()
    {
        await using var server = await StartAsync(LemonadeOnAnyAddress);
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);

        var saved = await admin.PutAsync(AdminProtocol.LemonadeBox, new LemonadeBox(_providers.LemonadeUrl, WorkingKey));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadAsStringAsync()).ShouldNotContain(WorkingKey);
        var shown = (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdKeys>>())!.Value.Lemonade.ShouldNotBeNull();
        shown.ApiUrl.ShouldBe(_providers.LemonadeUrl);
        shown.Status.ShouldBe(new KeyStatus(KeyState.Working, "work", server.Clock.GetUtcNow()));
        (await KeysSectionAsync(server, household)).Lemonade.ShouldBe(new LemonadeBox(_providers.LemonadeUrl, WorkingKey));
    }

    [Fact]
    public async Task ALemonadeBoxThatRefusesItsKey_IsNotSaved_AndOneRemovedIsGone()
    {
        await using var server = await StartAsync(LemonadeOnAnyAddress);
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        _providers.Accept(WorkingKey);
        await admin.PutAsync(AdminProtocol.LemonadeBox, new LemonadeBox(_providers.LemonadeUrl, WorkingKey));
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var refused = await admin.PutAsync(AdminProtocol.LemonadeBox, new LemonadeBox(_providers.LemonadeUrl, "a-key-it-refuses"));
        var removed = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.LemonadeBox));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("refused it (401)");
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await KeysSectionAsync(server, household)).Lemonade.ShouldBeNull();
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Removed the Lemonade box", "Set the Lemonade box"]);
    }

    // The agent dials the address with the household's key on every turn and every minute, from inside
    // the deployment: another household's box is a public https address, never one of the deployment's
    // own (a container name, loopback, a private network). The home's box is the deployment's own LAN.
    [Theory]
    [InlineData("http://box.example.org/api/v1")]
    [InlineData("https://127.0.0.1:8000/api/v1")]
    [InlineData("https://192.168.5.20/api/v1")]
    [InlineData("https://accounts:8080/accounts")]
    public async Task AHouseholdsLemonadeBox_IsAPublicHttpsAddress_AndNoneIsAsked(string apiUrl)
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.LemonadeBox, new LemonadeBox(apiUrl, WorkingKey));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("public https");
        _providers.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheHomesLemonadeBox_MayBeOnTheDeploymentsOwnNetwork()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        _providers.Accept(WorkingKey);

        var saved = await admin.PutAsync(AdminProtocol.LemonadeBox, new LemonadeBox(_providers.LemonadeUrl, WorkingKey));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static AccountsSettings LemonadeOnAnyAddress(AccountsSettings settings) =>
        settings with { KeyProbes = settings.KeyProbes with { LemonadeOnPrivateAddresses = true } };

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    private Task<AccountsServer> StartAsync(Func<AccountsSettings, AccountsSettings>? configure = null) =>
        AccountsServer.StartAsync(settings =>
        {
            var probed = settings with
            {
                KeyProbes = new KeyProbeSettings
                {
                    OpenRouterUrl = _providers.OpenRouterUrl,
                    IdealistaUrl = _providers.IdealistaUrl,
                    TelegramUrl = _providers.TelegramUrl
                }
            };
            return configure?.Invoke(probed) ?? probed;
        });

    private static async Task<KeysSection> KeysSectionAsync(AccountsServer server, string household) =>
        (await server.Service<global::Accounts.Configuration.HouseholdConfigStore>()
            .ReadAsync(household, HouseholdSections.KeysSchema, CancellationToken.None))!;
}