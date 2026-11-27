using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's spaces through the admin API (seam 1 of Tenants 4c): an owner adds, renames,
// recolours, reorders and removes spaces and picks the agent each opens on, saved through the spaces
// section's own save. The chat side is the spaces view the hub reads (ChatHub.GetSpaces), on the
// person's assertion for its channel, as the hub asks it.
public sealed class HouseholdSpacesAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";
    private const string HubCredential = "hub-credential";

    [Fact]
    public async Task AnOwner_ReadsTheHouseholdsSpaces_AndTheAgentsTheyMayOpenOn()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var spaces = await SpacesAsync(server.Admin(await server.MintSessionAsync(ana)));

        spaces.Spaces.ShouldBe([HouseholdSpace.Default]);
        spaces.Agents.ShouldBe([new AdminSpaceAgent("jonas", "Jonas"), new AdminSpaceAgent("sugary", "Sugary")]);
    }

    [Fact]
    public async Task ANewAndAnEditedSpace_AreWhatTheChatClientIsToldOfItsHousehold()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var hub = HubReader(server);
        var proof = HouseholdProof.OfAssertion(server.Service<global::Accounts.Signing.Assertions>().OfGuest(household, ChannelIds.SignalR));
        (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.ShouldBe([HouseholdSpace.Default]);
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", AccentColor = "#2a9d8f", DefaultAgent = "sugary", Order = 0 };
        var main = HouseholdSpace.Default with { Name = "Casa", AccentColor = "#112233", Order = 1 };

        var saved = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [main, kitchen] });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!;
        answer.Value.Spaces.ShouldBe([kitchen, main], "in their order");
        answer.Warning.ShouldBeNull();
        await Eventually.Until(async () => (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.Count == 2,
            "the announced save to reach the hub's reader");
        (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.ShouldBe([main, kitchen]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Added space kitchen; changed space default's name, colour, order"]);
    }

    [Fact]
    public async Task ARemovedSpace_IsNoLongerTheHouseholds()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var attic = new HouseholdSpace { Slug = "attic", Name = "Desván", Order = 1 };
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, attic] });
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var saved = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default] });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Removed space attic", "Added space attic"]);
    }

    // The default space is where a removed space's topics are listed, where an alarm's push lands
    // and what the chat's own address opens: it is renamed and recoloured, never removed.
    [Fact]
    public async Task TheDefaultSpace_IsNotRemoved_AndSaysWhy()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [new HouseholdSpace { Slug = "kitchen", Name = "Cocina" }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("Main");
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    // A space opens on one of the household's agents or on none; the section's own rules check only
    // that it names an id.
    [Fact]
    public async Task ASpaceOpeningOnAnAgentTheHouseholdDoesNotHave_IsRefused_AndNothingChanges()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default with { DefaultAgent = "butler" }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain("butler");
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Kitchen", "#2a9d8f", "Cocina")]
    [InlineData("kitchen", "teal", "Cocina")]
    [InlineData("kitchen", "#2a9d8f", " ")]
    public async Task ASpaceTheRulesRefuse_IsNotSaved(string slug, string colour, string name)
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = slug, Name = name, AccentColor = colour, Order = 1 }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldNotBeEmpty();
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
    }

    [Fact]
    public async Task ASpaceNameOverTheLimit_IsRefused()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default with { Name = new string('a', AdminSpaces.NameLength + 1) }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem()
            .ShouldContain(AdminSpaces.NameLength.ToString());
    }

    // Removing the agent a space opens on would leave the space opening on nothing the household has:
    // refused, as removing the household's default agent is, naming the space to change first.
    [Fact]
    public async Task AnAgentASpaceOpensOn_IsNotRemoved_AndSaysWhichSpace()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "kitchen", Name = "Cocina", DefaultAgent = "sugary", Order = 1 }]
        });

        var refused = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent("sugary")));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("Cocina");
        (await (await admin.GetAsync(AdminProtocol.Agents)).Content.ReadFromJsonAsync<AdminAgents>())!.Agents
            .Select(a => a.Id).ShouldBe(["jonas", "sugary"]);
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 1 }]
        });
        (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent("sugary")))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AMember_IsRefusedTheSpaces_AndChangesNothing()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(AdminProtocol.Spaces)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.Spaces, new SpacesSection())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // An owner shares a member's published app with the household from here, after seeing who made it,
    // what it reads and where it connects: the apps server's listing of the household's published apps,
    // asked service to service, the author named as the household knows them.
    [Fact]
    public async Task AnOwner_IsShownTheHouseholdsPublishedApps_WhoMadeThem_WhatTheyRead_AndWhereTheyConnect()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var bea = await server.SeedPersonAsync(household, "Bea");
        var id = await PublishedAsync(apps, new Account(household, bea), "glucose");
        await apps.AppAsync(new Account(household, bea), "draft", "Not published");

        var spaces = await SpacesAsync(server.Admin(await server.MintSessionAsync(ana)));

        var app = spaces.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        (app.Id, app.Slug, app.Name, app.AccentColor, app.AuthorId, app.AuthorName).ShouldBe((id, "glucose", "Glucose today", "#e9601f", bea, "Bea"));
        app.Reads.ShouldBe(["/sugary"]);
        app.Connect.ShouldBeEmpty();
        app.Url.ShouldBe($"https://glucose-{id}.{AppsServer.AppsDomain}/");
    }

    // A household the operator has not given the apps feature has no apps to share, and the apps server
    // is not asked.
    [Fact]
    public async Task AHouseholdWithoutTheAppsFeature_HasNoAppsToShare()
    {
        await using var server = await StartAsync(appsUrl: DeadEnd.Url());
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var spaces = await SpacesAsync(server.Admin(await server.MintSessionAsync(ana)));

        spaces.Apps.ShouldNotBeNull().ShouldBeEmpty();
    }

    // An app space's name and colour are its app's, whatever the page sent; it sits among the chat spaces
    // in the order the owner gave it, and opens on no agent.
    [Fact]
    public async Task AnAppSpace_IsTheAppsNameAndColour_InItsPlaceAmongTheChatSpaces()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var id = await PublishedAsync(apps, new Account(household, ana), "glucose");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 2 };

        var saved = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "glucose", Name = "Mine now", AccentColor = "#000000", AppId = id, Order = 1 }, kitchen]
        });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var shared = new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = id, Order = 1 };
        (await saved.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!.Value.Spaces.ShouldBe([HouseholdSpace.Default, shared, kitchen]);
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default, shared, kitchen]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe([$"Added space glucose, sharing app {id}; added space kitchen"]);
    }

    // A save answers the page what it loads: the household's published apps beside the spaces, whatever the
    // save held — unsharing the last app space leaves it to share again without a reload.
    [Fact]
    public async Task ASave_AnswersTheHouseholdsAppsToo_WhateverItHeld()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var id = await PublishedAsync(apps, new Account(household, ana), "glucose");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var shared = new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = id, Order = 1 };
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, shared] });

        var unshared = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default] });

        (await unshared.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!.Value.Apps.ShouldNotBeNull()
            .Select(a => a.Id).ShouldBe([id]);
    }

    // Only one of the household's published apps is shared: one not published yet, another household's
    // or no app at all is refused, naming the space, and nothing is saved.
    [Theory]
    [InlineData("unpublished")]
    [InlineData("elsewhere")]
    [InlineData("unknown")]
    public async Task AnAppSpaceForNoneOfTheHouseholdsPublishedApps_IsRefused(string which)
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var (elsewhere, stranger) = await server.SeedHouseholdAsync("Lejos", "Stranger");
        var id = which switch
        {
            "unpublished" => AppsServer.IdIn(await apps.AppAsync(new Account(household, ana), "draft")),
            "elsewhere" => await PublishedAsync(apps, new Account(elsewhere, stranger), "theirs"),
            _ => "zzzzzz"
        };
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "shared", Name = "Shared", AppId = id, Order = 1 }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain("space shared");
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
    }

    // What a space is never changes under its slug: an app space shares the app it was added with and
    // opens on no agent, and a chat space never becomes one, since its conversations would be left in a
    // space nobody can join. One app is one space, and the default space is a chat space.
    [Theory]
    [InlineData("another app", "set when the space is added")]
    [InlineData("a chat space", "holds conversations")]
    [InlineData("an app space opening on an agent", "opens on no agent")]
    [InlineData("one app, two spaces", "more than one space")]
    [InlineData("the default space", "space default holds conversations")]
    public async Task WhatASpaceIs_IsNeverChangedUnderItsSlug(string change, string said)
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var glucose = await PublishedAsync(apps, new Account(household, ana), "glucose");
        var lights = await PublishedAsync(apps, new Account(household, ana), "lights");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var shared = new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = glucose, Order = 1 };
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 2 };
        (await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, shared, kitchen] }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        IReadOnlyList<HouseholdSpace> sent = change switch
        {
            "another app" => [HouseholdSpace.Default, shared with { AppId = lights }, kitchen],
            "a chat space" => [HouseholdSpace.Default, shared, kitchen with { AppId = lights }],
            "an app space opening on an agent" => [HouseholdSpace.Default, shared with { DefaultAgent = "jonas" }, kitchen],
            "one app, two spaces" => [HouseholdSpace.Default, shared, kitchen, shared with { Slug = "again", Order = 3 }],
            _ => [HouseholdSpace.Default with { AppId = lights }, shared, kitchen]
        };
        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = sent });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldContain(p => p.Contains(said));
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default, shared, kitchen]);
    }

    // An owner shares an app approving what the page showed of its reach, capped at what it asks. A later
    // publish asking for more is shown as waiting on an owner; a save changing something else approves none of
    // it, since the approval is only ever what the page sent, and a save of the space approving it grants it.
    [Fact]
    public async Task AWiderPublish_IsShownPending_AndApprovedOnlyByASaveApprovingIt()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var author = new Account(household, ana);
        var id = await PublishedAsync(apps, author, "glucose");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        (await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces =
            [
                HouseholdSpace.Default,
                new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AppId = id, Order = 1, Approved = new AppReach { Reads = ["/sugary", "/notes"] } }
            ]
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var sharing = await SpacesAsync(admin);

        await apps.CreateAsync(author, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary","/notes"],"connect":["https://tracker.example.com"],"asks":["sugary","vault"]}""",
            overwrite: true);
        await apps.ExecAsync(author, "/glucose", "./publish");
        var widened = await SpacesAsync(admin);
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 2 };
        (await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, widened.Spaces[1], kitchen] }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var untouched = await SpacesAsync(admin);
        var app = widened.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        var approving = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, untouched.Spaces[1] with { Approved = app.Reach }, kitchen]
        });
        var approved = (await approving.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!.Value;

        sharing.Spaces[1].Approved.ShouldBe(new AppReach { Reads = ["/sugary"] });
        sharing.PendingOf(sharing.Spaces[1]).ShouldBe(AppReach.None);
        app.Asks.ShouldBe(["sugary", "vault"]);
        var pending = new AppReach { Reads = ["/notes"], Connect = ["https://tracker.example.com"], Asks = ["sugary", "vault"] };
        widened.PendingOf(widened.Spaces[1]).ShouldBe(pending);
        untouched.Spaces[1].Approved.ShouldBe(new AppReach { Reads = ["/sugary"] });
        untouched.PendingOf(untouched.Spaces[1]).ShouldBe(pending);
        approved.Spaces[1].Approved.ShouldBe(new AppReach
        {
            Reads = ["/sugary", "/notes"], Connect = ["https://tracker.example.com"], Asks = ["sugary", "vault"]
        });
        approved.PendingOf(approved.Spaces[1]).ShouldBe(AppReach.None);
        // The suite's clock stands still, so the log's order says nothing: only the approving save names the reach.
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(
            [$"Added space glucose, sharing app {id}", "Added space kitchen", "Changed space glucose's approved reach"],
            ignoreOrder: true);
    }

    // The servers an owner approves are only ever those the page sent, and no more of them than the app asks
    // now: one the app does not ask is dropped, in the app's order; one it asks that the page left out
    // stays unapproved. A second owner's page, loaded before, approving its connect alone keeps the servers.
    [Fact]
    public async Task ApprovedServers_AreWhatThePageSent_CappedAtWhatTheAppAsks_AndMergedWithAnotherOwnersApproval()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var author = new Account(household, ana);
        var id = await PublishedAsync(apps, author, "glucose");
        await apps.CreateAsync(author, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"],"connect":["https://tracker.example.com"],"asks":["sugary","vault","timers"]}""",
            overwrite: true);
        await apps.ExecAsync(author, "/glucose", "./publish");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var space = new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AppId = id, Order = 1 };

        (await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, space with { Approved = new AppReach { Reads = ["/sugary"], Asks = ["homeassistant", "vault", "sugary"] } }]
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var loaded = await SpacesAsync(admin);
        var mine = loaded.Spaces[1] with { Approved = loaded.Spaces[1].Approved! with { Asks = ["sugary", "vault", "timers"] } };
        var theirs = loaded.Spaces[1] with { Approved = loaded.Spaces[1].Approved! with { Connect = ["https://tracker.example.com"] } };
        var section = new SpacesSection { Spaces = [.. loaded.Spaces] };
        (await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(section with { Spaces = [HouseholdSpace.Default, mine] }, section)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var merging = await admin.PutAsync(AdminProtocol.Spaces, AdminEdit.Of(section with { Spaces = [HouseholdSpace.Default, theirs] }, section));

        loaded.Spaces[1].Approved.ShouldBe(new AppReach { Reads = ["/sugary"], Asks = ["sugary", "vault"] });
        loaded.PendingOf(loaded.Spaces[1]).ShouldBe(new AppReach { Connect = ["https://tracker.example.com"], Asks = ["timers"] });
        merging.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SpacesAsync(admin)).Spaces[1].Approved.ShouldBe(new AppReach
        {
            Reads = ["/sugary"], Connect = ["https://tracker.example.com"], Asks = ["sugary", "vault", "timers"]
        });
    }

    // The apps server not answering costs only what needs it: the spaces still load (their app spaces as
    // last saved) and save, and only adding an app space, or approving more of one's reach, waits for it.
    [Fact]
    public async Task AnAppsServerThatCannotBeAsked_StopsOnlyANewAppSpace_OrAnApproval()
    {
        await using var server = await StartAsync(appsUrl: DeadEnd.Url());
        var (household, ana) = await AppsHouseholdAsync(server);
        var shared = new HouseholdSpace { Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = "a1b2c3", Order = 1 };
        await server.Service<HouseholdConfigStore>().SaveAsync(household, HouseholdSections.SpacesSchema,
            HouseholdSections.SpacesSchema.Write(new SpacesSection { Spaces = [HouseholdSpace.Default, shared] }), CancellationToken.None);
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var loaded = await SpacesAsync(admin);
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 2 };
        var savedBeside = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, shared, kitchen] });
        var added = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, shared, kitchen, new HouseholdSpace { Slug = "lights", Name = "Lights", AppId = "d4e5f6", Order = 3 }]
        });

        var approving = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, shared with { Approved = new AppReach { Reads = ["/sugary"] } }, kitchen]
        });

        loaded.Spaces.ShouldBe([HouseholdSpace.Default, shared]);
        loaded.Apps.ShouldBeNull();
        savedBeside.StatusCode.ShouldBe(HttpStatusCode.OK);
        added.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await added.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldBe(AdminSpaces.AppsUnavailableMessage);
        // What the app asks cannot be read, so no approval can be held to it.
        approving.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default, shared, kitchen]);
    }

    // The apps server, verifying this service's assertions on a clock that agrees with it.
    private static Task<AppsServer> AppsAsync() =>
        AppsServer.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(AccountsServer.StartedAt))));

    private Task<AccountsServer> StartAsync(AppsServer apps) => StartAsync($"http://localhost:{apps.Settings.Listeners.Mcp}");

    // A household the operator gave the apps feature, and its owner.
    private static async Task<(string Household, string Owner)> AppsHouseholdAsync(AccountsServer server)
    {
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await server.Service<HouseholdConfigStore>().SaveAsync(
            household, HouseholdSections.FeaturesSchema, new JsonObject { ["apps"] = true }, CancellationToken.None);
        return (household, ana);
    }

    private static async Task<string> PublishedAsync(AppsServer apps, Account author, string slug)
    {
        var id = AppsServer.IdIn(await apps.AppAsync(author, slug, slug == "glucose" ? "Glucose today" : slug));
        await apps.CreateAsync(author, $"/{slug}/index.html", "<h1>Hi</h1>");
        await apps.ExecAsync(author, $"/{slug}", "./publish");
        return id;
    }

    // appsUrl: where the apps server listens; none is one that would never be asked.
    private Task<AccountsServer> StartAsync(string? appsUrl = null) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential, ["mcp-channel-signalr"] = HubCredential },
            Redis = new() { ConnectionString = redis.ConnectionString },
            // The suite's accounts key, which the apps server's verifier holds (TestAssertions).
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            AppsUrl = appsUrl ?? DeadEnd.Url()
        });

    // The chat hub's own reader, keeping what it read for longer than any test runs: only a save's
    // announcement makes it read again.
    private HouseholdConfigurationClient HubReader(AccountsServer server) =>
        new(server.Client(), HubCredential, null, redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    private static async Task<AdminSpaces> SpacesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.Spaces)).Content.ReadFromJsonAsync<AdminSpaces>())!;

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}