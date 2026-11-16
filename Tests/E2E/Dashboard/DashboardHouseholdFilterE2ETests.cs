using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.DTOs.Admin;
using Domain.DTOs.Metrics;
using Microsoft.Playwright;
using Shouldly;
using StackExchange.Redis;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

// The dashboard's household filter (seam 3 of Tenants 4c), through the dashboard's own host: the
// operator picks a household and every metric page shows its figures alone — what was rolled up for
// it, and what arrives live — named as the household list names it; all households shows everybody's.
[Collection("DashboardAdminE2E")]
[Trait("Category", "E2E")]
public partial class DashboardHouseholdFilterE2ETests(DashboardAdminE2EFixture fixture)
{
    private static readonly JsonSerializerOptions _wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task TheOperator_PicksAHousehold_AndEveryPageShowsItsFiguresAlone()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var (page, home) = await fixture.CreateOperatorPageAsync();
        var casaName = $"Casa {tag}";
        var casa = await CreateHouseholdAsync(page, casaName);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        await PublishAsync(redis, Tokens(home, $"fran-{tag}", 1_111));
        await PublishAsync(redis, Tokens(casa, $"ana-{tag}", 2_222));

        await page.GotoAsync(fixture.DashboardUrl);
        await page.Locator(".connection-status.live").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        var picker = page.GetByLabel("Household");
        await Assertions.Expect(picker.Locator("option", new LocatorLocatorOptions { HasText = casaName }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await picker.SelectOptionAsync(new SelectOptionValue { Label = casaName });
        await ExpectInputTokensAsync(page, 2_222);

        await picker.SelectOptionAsync(new SelectOptionValue { Label = "Home" });
        await ExpectInputTokensAsync(page, 1_111);

        await picker.SelectOptionAsync(new SelectOptionValue { Label = casaName });
        await ExpectInputTokensAsync(page, 2_222);

        // Live: the home's push is not Casa's, and Casa's is.
        await PublishAsync(redis, Tokens(home, $"fran-{tag}", 5_000));
        await Eventually.Settle();
        (await InputTokensAsync(page)).ShouldBe(2_222, "a push for another household is not shown");
        await PublishAsync(redis, Tokens(casa, $"ana-{tag}", 1));
        await ExpectInputTokensAsync(page, 2_223);

        // Another page, the household still picked.
        await page.Locator("nav a[title=Tokens]").ClickAsync();
        var rows = page.Locator(".events-table .table-row");
        await Assertions.Expect(rows.Filter(new LocatorFilterOptions { HasText = $"ana-{tag}" }))
            .ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        (await rows.Filter(new LocatorFilterOptions { HasText = $"fran-{tag}" }).CountAsync())
            .ShouldBe(0, "the home's events are not Casa's");

        await page.GetByLabel("Household").SelectOptionAsync(new SelectOptionValue { Label = "All households" });
        await Assertions.Expect(rows.Filter(new LocatorFilterOptions { HasText = $"fran-{tag}" }))
            .ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
    }

    private static TokenUsageEvent Tokens(string household, string sender, int input) => new()
    {
        TenantId = household, Sender = sender, Model = "e2e-model", InputTokens = input, OutputTokens = 0, Cost = 0m,
        AgentId = "jonas", AgentName = "Jonas", Timestamp = DateTimeOffset.UtcNow
    };

    private static Task PublishAsync(ConnectionMultiplexer redis, MetricEvent evt) =>
        redis.GetSubscriber().PublishAsync(RedisChannel.Literal("metrics:events"), JsonSerializer.Serialize(evt, _wire));

    private async Task<string> CreateHouseholdAsync(IPage page, string name)
    {
        var created = await page.APIRequest.PostAsync($"{fixture.DashboardUrl}{AdminProtocol.Households.TrimStart('/')}",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { [AdminProtocol.RequestHeader] = AdminProtocol.RequestHeaderValue },
                DataObject = new NewHousehold(name, "Ana")
            });
        created.Ok.ShouldBeTrue(await created.TextAsync());
        var saved = await created.JsonAsync<AdminSaved<OwnerLink>>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return saved!.Value.HouseholdId;
    }

    private static async Task ExpectInputTokensAsync(IPage page, long expected) =>
        await Assertions.Expect(page.Locator(".kpi-card").Filter(new LocatorFilterOptions { HasText = "Input Tokens" })
                .Locator(".kpi-value"))
            .ToHaveTextAsync(new Regex($"^\\D*{string.Join("\\D?", expected.ToString().ToCharArray())}\\D*$"),
                new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });

    private static async Task<long> InputTokensAsync(IPage page) =>
        long.Parse(NotDigits().Replace(
            await page.Locator(".kpi-card").Filter(new LocatorFilterOptions { HasText = "Input Tokens" })
                .Locator(".kpi-value").TextContentAsync() ?? "", ""));

    [GeneratedRegex(@"\D")]
    private static partial Regex NotDigits();
}