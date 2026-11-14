using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Shouldly;
using Tests.Integration.Fixtures;
using WebChat.Client.State.Admin;

namespace Tests.Integration.Accounts;

// Two owners with the same admin page open: each page sends what it loaded beside what it wants, and
// the admin API lays what it changed over the section as it is now, so the other owner's change to
// anything else stands. Only a field both changed, to different values, is refused — with nothing
// saved and nothing recorded — and the refusal names it.
public sealed class ConcurrentAdminEditsTests
{
    [Fact]
    public async Task TwoOwnersEachAddingASpace_FromPagesOpenedTogether_KeepBothSpaces()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var loaded = new SpacesSection { Spaces = [HouseholdSpace.Default] };
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Kitchen", Order = 1 };
        var kids = new HouseholdSpace { Slug = "kids", Name = "Kids", Order = 2 };

        (await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(loaded with { Spaces = [HouseholdSpace.Default, kitchen] }, loaded)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(loaded with { Spaces = [HouseholdSpace.Default, kids] }, loaded));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!.Value.Spaces.Select(s => s.Slug)
            .ShouldBe(["default", "kitchen", "kids"]);
    }

    [Fact]
    public async Task ASpaceRemovedOnOnePage_AfterAnotherRenamedIt_IsRefusedNamingIt_AndNothingChanges()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var attic = new HouseholdSpace { Slug = "attic", Name = "Attic", Order = 1 };
        var loaded = new SpacesSection { Spaces = [HouseholdSpace.Default, attic] };
        await admin.PutAsync(AdminProtocol.Spaces, loaded);
        await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(new SpacesSection { Spaces = [HouseholdSpace.Default, attic with { Name = "Loft" }] }, loaded));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(new SpacesSection { Spaces = [HouseholdSpace.Default] }, loaded));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("space attic");
        var spaces = (await (await admin.GetAsync(AdminProtocol.Spaces)).Content.ReadFromJsonAsync<AdminSpaces>())!;
        spaces.Spaces.Select(s => s.Name).ShouldBe(["Main", "Loft"]);
    }

    // Each page removes a different space, as the spaces page builds its save: Bea's removal of the
    // attic was refused because Ana's removal of the kitchen had numbered the attic's order again.
    [Fact]
    public async Task TwoOwnersEachRemovingADifferentSpace_FromPagesOpenedTogether_BothStand()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var start = new SpaceDrafts([HouseholdSpace.Default]);
        start.Add("kitchen", "Kitchen");
        start.Add("attic", "Attic");
        start.Add("garden", "Garden");
        await admin.PutAsync(AdminProtocol.Spaces, start.ToSection());
        var loaded = (await (await admin.GetAsync(AdminProtocol.Spaces)).Content.ReadFromJsonAsync<AdminSpaces>())!.Spaces;
        var anas = new SpaceDrafts(loaded);
        var beas = new SpaceDrafts(loaded);
        anas.Remove(1);
        beas.Remove(2);

        (await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(anas.ToSection(), new SpacesSection { Spaces = loaded })))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(beas.ToSection(), new SpacesSection { Spaces = loaded }));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!.Value.Spaces.Select(s => s.Slug)
            .ShouldBe(["default", "garden"]);
    }

    [Fact]
    public async Task TwoOwnersTurningOffDifferentFeatures_BothStand()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var loaded = new FeaturesSection();

        await admin.PutAsync(AdminProtocol.Features, AdminEdit.Of(loaded with { Memory = false }, loaded));
        var second = await admin.PutAsync(AdminProtocol.Features, AdminEdit.Of(loaded with { Timers = false }, loaded));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await second.Content.ReadFromJsonAsync<AdminSaved<FeaturesSection>>())!.Value;
        saved.Memory.ShouldBeFalse();
        saved.Timers.ShouldBeFalse();
    }

    [Fact]
    public async Task OneOwnerRenamingAndAnotherMovingTheZone_BothStand_AndTheSameFieldTwiceIsRefused()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var loaded = (await (await admin.GetAsync(AdminProtocol.Settings)).Content.ReadFromJsonAsync<HouseholdSettings>())!;

        await admin.PutAsync(AdminProtocol.Settings, AdminEdit.Of(loaded with { Name = "Casa Kapoor" }, loaded));
        var zone = await admin.PutAsync(AdminProtocol.Settings, AdminEdit.Of(loaded with { TimeZone = "Asia/Kolkata" }, loaded));
        var renamedAgain = await admin.PutAsync(AdminProtocol.Settings, AdminEdit.Of(loaded with { Name = "Kapoor House" }, loaded));

        zone.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await zone.Content.ReadFromJsonAsync<AdminSaved<HouseholdSettings>>())!.Value;
        (saved.Name, saved.TimeZone).ShouldBe(("Casa Kapoor", "Asia/Kolkata"));
        renamedAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await renamedAgain.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("the name");
    }

    [Fact]
    public async Task OneOwnerRenamingAnAgentAndAnotherChangingItsInstructions_BothStand()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var loaded = new AgentEdit("Jonas", null);

        await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Dadi", null) { Loaded = loaded });
        var second = await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Jonas", "Answer in short sentences.") { Loaded = loaded });

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jonas = (await second.Content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!.Value.Agents.Single(a => a.Id == "jonas");
        (jonas.Name, jonas.CustomInstructions).ShouldBe(("Dadi", "Answer in short sentences."));
    }
}