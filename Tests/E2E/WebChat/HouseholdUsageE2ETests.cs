using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.DTOs.Admin;
using Domain.DTOs.Metrics;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The admin area's usage page in the chat client (seam 2 of Tenants 4c). The stack runs no
// Observability host, so what the page reads for the real admin API is that the figures could not be
// read; a month's figures are shown by answering the page's usage read as the admin API answers it.
// That an owner reads their own household's usage and never another's is the admin API's seam
// (HouseholdUsageAdminTests), against the Observability host's own gate and roll-ups.
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class HouseholdUsageE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_ReadsAMonthsUsage_PerAgentAndPerson_AndLemonade_AndPicksAnotherMonth()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        // No service worker: a request it makes for the page is one page.RouteAsync never sees.
        var page = await fixture.CreatePageSignedInAsync(
            await OwnerSessionAsync($"Owner {Guid.NewGuid().ToString("N")[..4]}"), withoutServiceWorker: true);
        var asked = new List<string?>();
        await page.RouteAsync(url => new Uri(url).AbsolutePath == AdminProtocol.Usage, async route =>
        {
            var month = new Uri(route.Request.Url).Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=')).Where(p => p[0] == "month").Select(p => p[1]).FirstOrDefault();
            asked.Add(month);
            var usage = month == "2026-09"
                ? new AdminUsage("2026-09", [new UsageLine("Jonas", 40, 0.02m)], [new UsageLine("Ana", 40, 0.02m)],
                    new LemonadeUsage(0, 0, 0))
                : new AdminUsage("2026-10",
                    [new UsageLine("Jonas", 12_345, 0.4567m), new UsageLine("Sugary", 600, 0.01m)],
                    [new UsageLine("Ana", 12_000, 0.45m), new UsageLine("Luis", 945, 0.0167m)],
                    new LemonadeUsage(75.5, 1_200, 9));
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(usage, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        });

        // From the stack's root: the collection's own URL carries its space's slug.
        await page.GotoAsync($"{fixture.DefaultSpaceUrl}admin/usage");

        var agents = page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Per agent" });
        await Assertions.Expect(agents.GetByRole(AriaRole.Row).Nth(1))
            .ToContainTextAsync("Jonas", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(agents.GetByRole(AriaRole.Row).Nth(1)).ToContainTextAsync("12,345");
        await Assertions.Expect(agents.GetByRole(AriaRole.Row).Nth(1)).ToContainTextAsync("$0.4567");
        await Assertions.Expect(agents.GetByRole(AriaRole.Row).Nth(2)).ToContainTextAsync("Sugary");
        var people = page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Per person" });
        await Assertions.Expect(people.GetByRole(AriaRole.Row).Nth(1)).ToContainTextAsync("Ana");
        await Assertions.Expect(people.GetByRole(AriaRole.Row).Nth(2)).ToContainTextAsync("Luis");
        var lemonade = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Speech and memory" });
        await Assertions.Expect(lemonade).ToContainTextAsync("75.5 seconds heard");
        await Assertions.Expect(lemonade).ToContainTextAsync("1,200 characters spoken");
        await Assertions.Expect(lemonade).ToContainTextAsync("9 embeddings");
        await Assertions.Expect(page.GetByLabel("Month")).ToHaveValueAsync("2026-10");

        await page.GetByLabel("Month").FillAsync("2026-09");

        await Assertions.Expect(agents.GetByRole(AriaRole.Row).Nth(1)).ToContainTextAsync("40");
        (await agents.GetByRole(AriaRole.Row).CountAsync()).ShouldBe(2, "a header and September's one agent");
        asked.ShouldContain("2026-09");
    }

    // The stack's accounts service has no Observability host to ask, as a deployment whose host is down.
    [SkippableFact]
    public async Task WithTheFiguresUnreadable_TheOwnerIsToldSo()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync($"Owner {Guid.NewGuid().ToString("N")[..4]}"));

        await page.GotoAsync($"{fixture.DefaultSpaceUrl}admin/usage");

        await Assertions.Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("could not be read", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        (await page.GetByRole(AriaRole.Table).CountAsync()).ShouldBe(0, "no figures are shown as nothing used");
    }

    // The stack's people are members; an owner is made for the test, as the operator's command makes one.
    private async Task<string> OwnerSessionAsync(string name)
    {
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name, "--role", "owner");
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        var (_, session) = await fixture.AccountsAsync("mint-session", "--user", user);
        return session.Trim();
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household")]
    private static partial Regex WhoWasMade();
}