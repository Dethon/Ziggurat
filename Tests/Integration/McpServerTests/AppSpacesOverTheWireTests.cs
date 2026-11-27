using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// An app space from end to end, against a real accounts service on a real Redis: an owner shares a
// published app from the spaces page (the accounts service asking the apps server's listing), the apps
// server reads the household's spaces on each viewer's own `apps` assertion, so another member signed in
// through the app sign-in opens it; a disabled member's app sessions end with their sessions, another
// household's member gets the 404, and removing the space unshares it.
public class AppSpacesOverTheWireTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task AnAppSpace_OpensForTheHousehold_UntilItIsRemoved()
    {
        var appsPort = TestPort.GetAvailable();
        await using var accounts = await AccountsServer.StartAsync(s => s with
        {
            AppsDomain = AppsServer.AppsDomain,
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            Callers = new Dictionary<string, string> { ["mcp-apps"] = McpTestSecret.Gate.SharedSecret },
            Redis = new RedisSettings { ConnectionString = redis.ConnectionString },
            AppsUrl = $"http://localhost:{appsPort}"
        });
        await using var apps = await AppsServer.StartAsync(
            s => s with
            {
                PublicBaseUrl = accounts.Address.ToString().TrimEnd('/'),
                Accounts = s.Accounts with { BaseUrl = accounts.Address.ToString() },
                RedisConnectionString = redis.ConnectionString
            },
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(accounts.Clock));
                services.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(sp =>
                    new HouseholdConfigFromAccounts(sp.GetRequiredService<HouseholdConfigurationClient>())));
            },
            appsPort);
        var fran = await accounts.MintOperatorAsync("Fran");
        var laura = await accounts.SeedMemberAsync("Laura");
        var dani = await accounts.SeedMemberAsync("Dani");
        var (elsewhere, stranger) = await accounts.SeedHouseholdAsync("Lejos", "Stranger");
        await accounts.Service<HouseholdConfigStore>().SaveAsync(
            fran.TenantId, HouseholdSections.FeaturesSchema, new JsonObject { ["apps"] = true }, CancellationToken.None);
        var author = new Account(fran.TenantId, fran.UserId);
        var id = AppsServer.IdIn(await apps.AppAsync(author, "glucose"));
        await apps.CreateAsync(author, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"],"connect":["https://tracker.example.com"]}""",
            overwrite: true);
        await apps.CreateAsync(author, "/glucose/index.html", "<h1>Glucose</h1>");
        await apps.ExecAsync(author, "/glucose", "./publish");
        var host = apps.Published("glucose", id);
        var admin = accounts.Admin(await accounts.MintSessionAsync(fran.UserId));
        var hers = await apps.SignInThroughAsync(accounts, host, "/", await accounts.MintSessionAsync(laura));
        (await apps.GetAsync(host, "/", hers.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "not shared yet");

        var offered = (await (await admin.GetAsync(AdminProtocol.Spaces)).Content.ReadFromJsonAsync<AdminSpaces>())!;
        var app = offered.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        var shared = await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(
            new SpacesSection
            {
                Spaces = [.. offered.Spaces, new HouseholdSpace { Slug = "glucose", Name = app.Name, AppId = app.Id, Order = 1, Approved = app.Reach }]
            },
            new SpacesSection { Spaces = offered.Spaces }));

        shared.StatusCode.ShouldBe(HttpStatusCode.OK, await shared.Content.ReadAsStringAsync());
        await Eventually.Until(async () => (await apps.GetAsync(host, "/", hers.Token, navigate: true)).StatusCode == HttpStatusCode.OK,
            "the shared app to open for another member");
        // The approval crossed the spaces view whole: the member's page is granted the origin it approved.
        (await apps.GetAsync(host, "/", hers.Token, navigate: true)).Headers.GetValues("Content-Security-Policy").Single()
            .Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith("connect-src ", StringComparison.Ordinal))
            .ShouldContain("https://tracker.example.com");
        var his = await apps.SignInThroughAsync(accounts, host, "/", await accounts.MintSessionAsync(dani));
        (await apps.GetAsync(host, "/", his.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var theirs = await apps.SignInThroughAsync(accounts, host, "/", await accounts.MintSessionAsync(stranger));
        (await apps.GetAsync(host, "/", theirs.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        elsewhere.ShouldNotBe(fran.TenantId);

        (await admin.PostAsync(AdminProtocol.DisableMember(dani))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.Until(async () => (await apps.GetAsync(host, "/", his.Token)).StatusCode == HttpStatusCode.NotFound,
            "the disabled member's app session to end with their sessions");
        (await apps.GetAsync(host, "/", hers.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var removed = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default] });

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.Until(async () => (await apps.GetAsync(host, "/", hers.Token)).StatusCode == HttpStatusCode.NotFound,
            "the unshared app to close for another member");
        var own = await apps.SignInThroughAsync(accounts, host, "/", await accounts.MintSessionAsync(fran.UserId));
        (await apps.GetAsync(host, "/", own.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK, "still its author's");
    }
}