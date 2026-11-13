using System.Net;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// Two owners saving one member from two pages at once, in a collection of its own: the case is a
// third of the members page's time, and in one chain with the rest it was the run's tail.
[Collection(WebChatE2ECollections.MembersConcurrent)]
[Trait("Category", "E2E")]
public class HouseholdMembersConcurrentE2ETests(WebChatE2EFixture fixture) : HouseholdMembersE2EBase(fixture)
{
    // Two owners with the page open: one makes a member an owner, the other — whose page still says
    // Member — ticks the member's sees-all and saves. The second save used to send the role it had
    // loaded beside the tick and made the member a member again, nobody told.
    [SkippableFact]
    public async Task TwoOwnersChangingOneMember_EachChangeStands()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var name = $"Member {tag}";
        await SessionOfAsync(name, "member");
        var first = await MembersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));
        await using var secondContext = await ContextOfAsync(first, await SessionOfAsync($"Second owner {tag}", "owner"));
        var second = await MembersPageAsync(await secondContext.NewPageAsync());
        var firstRow = first.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        var secondRow = second.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(firstRow).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(secondRow).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        await firstRow.Locator("select[name=role]").SelectOptionAsync("owner");
        await firstRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(first.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await secondRow.GetByLabel("Sees all conversations").CheckAsync();
        await secondRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(second.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await first.ReloadAsync();
        firstRow = first.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(firstRow.GetByLabel("Sees all conversations")).ToBeCheckedAsync(new LocatorAssertionsToBeCheckedOptions { Timeout = 30_000 });
        await Assertions.Expect(firstRow.Locator("select[name=role]")).ToHaveValueAsync("owner");
    }
}