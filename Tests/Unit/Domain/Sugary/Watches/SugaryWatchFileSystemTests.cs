using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Security;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary.Watches;
using Domain.Tools.Watches;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Domain.Tools.FileSystem;

namespace Tests.Unit.Domain.Sugary.Watches;

// Glucose watches at /watchers, beside the home's: driven through the union exactly as the agent's
// tools reach them, the home's watches an in-memory fallback member. A glucose watch is the business of
// its subject, its writer and whoever holds everyone's Sugary log, and nobody else's.
public class SugaryWatchFileSystemTests
{
    private const string Household = "household-home";
    private const string Dethon = "user-dethon";
    private const string Tradaly = "user-tradaly";
    private const string Dani = "user-dani";

    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-18T10:00:00Z");

    private readonly InMemoryGlucoseWatches _store = new();
    private readonly FakeTimeProvider _time = new(_now);
    private readonly Mock<ISatelliteCatalog> _satellites = new();
    private readonly Mock<IHouseholdVoices> _voices = new();

    private readonly EntryMount _home = new("ha-watches", new Dictionary<string, string>
    {
        ["porch/watch.json"] = """{"triggers":[{"trigger":"state"}]}""",
        ["porch/status.json"] = """{"enabled":true}"""
    });

    // Dethon reads everyone's log; Tradaly and Dani their own.
    private HouseholdSugaryPeople _people = new([
        new SugaryPerson(Dethon, "Dethon", true),
        new SugaryPerson(Tradaly, "Tradaly", false),
        new SugaryPerson(Dani, "Dani", false)
    ]);

    public SugaryWatchFileSystemTests()
    {
        IReadOnlyList<SatelliteDescriptor> roster = [new("kitchen-01", "kitchen"), new("office-01", "office")];
        _satellites.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(roster);
        _satellites.Setup(s => s.ResolveAsync(It.IsAny<AnnounceTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AnnounceTarget t, CancellationToken _) => roster
                .Where(s => t.All == true || s.Room == t.Room || s.Id == t.SatelliteId || (t.SatelliteIds?.Contains(s.Id) ?? false))
                .Select(s => s.Id).ToList());
        _voices.Setup(v => v.For(Household)).Returns(
            new HouseholdVoice(Mock.Of<IInsistentAnnouncer>(), Mock.Of<IAlertDismisser>(), _satellites.Object));
    }

    private const string LowWatch = """
        {"name":"Low","triggers":[{"trigger":"glucose","below":70,"for":{"minutes":10}}],
         "effects":[{"kind":"prompt","prompt":"Tell her she is low"}]}
        """;

    private static string LowFor(string person) =>
        $$"""
          {"name":"Low","triggers":[{"trigger":"glucose","below":70,"person":"{{person}}"}],
           "effects":[{"kind":"prompt","prompt":"Tell them they are low"}]}
          """;

    [Fact]
    public async Task AGlucoseWatch_IsWrittenReadEditedAndRemoved_ThroughTheUnion()
    {
        var union = Union(Tradaly);

        Ok(await union.CreateAsync("low/watch.json", LowWatch, false, true, CancellationToken.None)).Status.ShouldBe("created");
        _home.Files.Keys.ShouldNotContain("low/watch.json");

        var read = Ok(await union.ReadAsync("low/watch.json", null, null, CancellationToken.None)).Content;
        read.ShouldContain("\"below\": 70");
        read.ShouldContain("\"minutes\": 10");

        Ok(await union.EditAsync("low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 65")], CancellationToken.None));
        GlucoseWatchSpec.Parse(_store.Stored(Household, "low")!.Spec).Triggers.Single()
            .ShouldBeOfType<GlucoseLevelTrigger>().MgPerDl.ShouldBe(65);

        Ok(await union.GlobAsync("", "*", CancellationToken.None)).Entries.ShouldBe(["low/", "porch/"]);
        Ok(await union.GlobAsync("", "**/*", CancellationToken.None)).Entries
            .ShouldBe(["low/", "low/status.json", "low/watch.json", "porch/status.json", "porch/watch.json"]);

        Ok(await union.DeleteAsync("low", CancellationToken.None));
        _store.Stored(Household, "low").ShouldBeNull();
        _home.Files.Keys.ShouldContain("porch/watch.json");
    }

    [Fact]
    public async Task AHomeWatch_StillGoesToTheHome()
    {
        Ok(await Union(Tradaly).CreateAsync("door/watch.json", """{"triggers":[{"trigger":"state"}]}""", false, true, CancellationToken.None));

        _home.Files.Keys.ShouldContain("door/watch.json");
        _store.Stored(Household, "door").ShouldBeNull();
    }

    [Fact]
    public async Task AWrite_RemembersWhoItIsAbout_WhoWroteIt_AndTheAgent()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var stored = _store.Stored(Household, "low")!;
        stored.SubjectUserId.ShouldBe(Tradaly);
        stored.WriterUserId.ShouldBe(Tradaly);
        stored.AgentId.ShouldBe("jonas");
        stored.CreatedAt.ShouldBe(_now);
        stored.Enabled.ShouldBeTrue();
        stored.Once.ShouldBeFalse();
        stored.Delegation.ShouldBeNull();
    }

    // "Warn me" is answered where it was asked, and the fire runs for the person who asked: a file naming
    // neither takes the caller's, as a home watch does.
    [Fact]
    public async Task DeliverToAndUserId_AreTheCallersWhenTheFileNamesNone()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var stored = _store.Stored(Household, "low")!;
        stored.DeliverTo.ShouldBe(["signalr"]);
        stored.UserId.ShouldBe(Tradaly);
        var read = JsonNode.Parse(await ReadAsync(Tradaly, "low/watch.json"))!;
        read["deliverTo"]!.AsArray().Select(d => d!.GetValue<string>()).ShouldBe(["signalr"]);
        read["userId"]!.GetValue<string>().ShouldBe(Tradaly);
    }

    [Fact]
    public async Task AReplaceNamingNoDelivery_KeepsTheWatchsOwn()
    {
        await CreateAsync(Tradaly, "low", LowWatch.Replace("\"name\":\"Low\"", "\"name\":\"Low\",\"deliverTo\":[\"telegram\"]"));

        Ok(await Mount(Tradaly).CreateAsync("low/watch.json", LowWatch, true, true, CancellationToken.None)).Status.ShouldBe("replaced");

        _store.Stored(Household, "low")!.DeliverTo.ShouldBe(["telegram"]);
    }

    [Theory]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":"low"}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "triggers[0].below must be a number")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"actions","actions":[{"action":"light.turn_on"}]}]}""", "effects[0].kind 'actions'")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"conditions":[{"condition":"state"}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "conditions[0].condition 'state'")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"sensor","state":"ending"}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "triggers[0].within is required")]
    public async Task ARefusedField_IsNamed_AndNothingIsKept(string content, string expected)
    {
        var refused = Error(await Union(Tradaly).CreateAsync("bad/watch.json", content, false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        refused.Message.ShouldContain(expected);
        _store.Stored(Household, "bad").ShouldBeNull();
    }

    [Fact]
    public async Task AnInvalidId_IsRefused()
    {
        Error(await Mount(Tradaly).CreateAsync("my watch/watch.json", LowWatch, false, true, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task StatusJson_IsReadOnly()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        Error(await Union(Tradaly).EditAsync("low/status.json", [new TextEdit("a", "b")], CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        Error(await Mount(Tradaly).CreateAsync("low/status.json", "{}", true, true, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // Who is watched, when the watch was made and last fired, and — once the engine has judged a
    // reading — whether each trigger's condition holds and since when.
    [Fact]
    public async Task StatusJson_SaysWhenItWasMade_WhoItWatches_AndWhatHoldsNow()
    {
        await CreateAsync(Dethon, "her-low", """
            {"name":"Low","triggers":[{"trigger":"glucose","below":70,"person":"tradaly"},{"trigger":"sensor","state":"ending","within":{"hours":24},"person":"tradaly"}],
             "effects":[{"kind":"prompt","prompt":"x"}]}
            """);

        var before = JsonNode.Parse(await ReadAsync(Dethon, "her-low/status.json"))!;
        before["createdAt"]!.GetValue<string>().ShouldBe("2026-11-18T10:00:00+00:00");
        before["lastFiredAt"].ShouldBeNull();
        before["spent"]!.GetValue<bool>().ShouldBeFalse();
        before["subject"]!.GetValue<string>().ShouldBe("Tradaly");
        before["person"]!.GetValue<string>().ShouldBe("tradaly");
        before["triggers"]!.AsArray().Select(t => (t!["trigger"]!.GetValue<string>(), t["holds"])).ShouldBe(
            [("glucose", null), ("sensor", null)]);

        _store.Judged(Household, "her-low", new GlucoseTriggerCondition(0, true, _now.AddMinutes(-5), true));
        var after = JsonNode.Parse(await ReadAsync(Dethon, "her-low/status.json"))!;
        after["triggers"]![0]!["holds"]!.GetValue<bool>().ShouldBeTrue();
        after["triggers"]![0]!["since"]!.GetValue<string>().ShouldBe("2026-11-18T09:55:00+00:00");
        after["triggers"]![1]!["holds"].ShouldBeNull();
    }

    [Fact]
    public async Task ACallNamingNobody_IsRefusedEverywhere()
    {
        await CreateAsync(Tradaly, "low", LowWatch);
        var nobody = new SugaryWatchFileSystem(_store, _voices.Object, TimeZoneInfo.Utc, _time).For(FileSystemCaller.Nobody);

        Error(await nobody.GlobAsync("", "*", CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await nobody.ReadAsync("low/watch.json", null, null, CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await nobody.ReadBlobAsync("low/watch.json", 0, 100, CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await nobody.InfoAsync("low", CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await nobody.CreateAsync("other/watch.json", LowWatch, false, true, CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await nobody.DeleteAsync("low", CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
    }

    // The union asks every member; one refusing a nobody caller adds nothing, so the home's watches are
    // still listed.
    [Fact]
    public async Task ACallNamingNobody_StillListsTheHomesWatches()
    {
        Ok(await Union(caller: null).GlobAsync("", "*", CancellationToken.None)).Entries.ShouldBe(["porch/"]);
    }

    [Fact]
    public async Task AnotherPersonsSensor_WithoutTheGrant_IsPermissionDenied()
    {
        var refused = Error(await Union(Dani).CreateAsync("her-low/watch.json", LowFor("tradaly"), false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        refused.Message.ShouldContain("triggers[0].person");
        _store.Stored(Household, "her-low").ShouldBeNull();
    }

    [Fact]
    public async Task AnotherPersonsSensor_WithTheGrant_IsWatched()
    {
        await CreateAsync(Dethon, "her-low", LowFor("tradaly"));

        var stored = _store.Stored(Household, "her-low")!;
        stored.SubjectUserId.ShouldBe(Tradaly);
        stored.WriterUserId.ShouldBe(Dethon);
    }

    [Fact]
    public async Task APersonNobodyGoesBy_IsRefusedNamingWhoIsThere()
    {
        var refused = Error(await Mount(Dethon).CreateAsync("x/watch.json", LowFor("laura"), false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        refused.Message.ShouldContain("triggers[0].person 'laura'");
        refused.Message.ShouldContain("tradaly");
    }

    // Her own folder name is her own sensor, with or without the grant.
    [Fact]
    public async Task NamingOnesOwnFolder_WatchesOnesOwnSensor()
    {
        await CreateAsync(Dani, "mine", LowFor("dani"));

        _store.Stored(Household, "mine")!.SubjectUserId.ShouldBe(Dani);
    }

    [Fact]
    public async Task TriggersNamingTwoPeople_AreRefused()
    {
        var refused = Error(await Mount(Dethon).CreateAsync("both/watch.json", """
            {"name":"Both","triggers":[{"trigger":"glucose","below":70},{"trigger":"glucose","below":70,"person":"tradaly"}],
             "effects":[{"kind":"prompt","prompt":"x"}]}
            """, false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        refused.Message.ShouldContain("triggers[1]");
        refused.Message.ShouldContain("one person");
    }

    [Fact]
    public async Task AWatch_IsListedForItsSubjectItsWriterAndTheGrantsHolders_AndNobodyElse()
    {
        // Dethon writes one on Tradaly; Tradaly one on herself; Dani one on herself.
        await CreateAsync(Dethon, "her-low", LowFor("tradaly"));
        await CreateAsync(Tradaly, "tradaly-high", LowWatch);
        await CreateAsync(Dani, "dani-low", LowWatch);

        (await Ids(Dethon)).ShouldBe(["dani-low", "her-low", "tradaly-high"]);
        (await Ids(Tradaly)).ShouldBe(["her-low", "tradaly-high"]);
        (await Ids(Dani)).ShouldBe(["dani-low"]);

        Error(await Union(Dani).ReadAsync("her-low/watch.json", null, null, CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        Ok(await Mount(Dani).InfoAsync("her-low", CancellationToken.None)).Exists.ShouldBeFalse();
        Error(await Mount(Dani).EditAsync("her-low/watch.json", [new TextEdit("70", "60")], CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        Error(await Mount(Dani).DeleteAsync("her-low", CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        _store.Stored(Household, "her-low").ShouldNotBeNull();
    }

    // A grant withdrawn is a grant gone: the writer still sees what they wrote, others' watches they did
    // not write go out of view.
    [Fact]
    public async Task AWithdrawnGrant_LeavesTheWriterTheirOwnWatchesOnly()
    {
        await CreateAsync(Dethon, "her-low", LowFor("tradaly"));
        await CreateAsync(Dani, "dani-low", LowWatch);

        _people = new HouseholdSugaryPeople([.. _people.People.Select(p => p with { ReadsEveryLog = false })]);

        (await Ids(Dethon)).ShouldBe(["her-low"]);
        // Still Tradaly's sensor, which he may no longer watch: an edit is refused.
        Error(await Mount(Dethon).EditAsync("her-low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 60")], CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
    }

    // An id is the household's: one somebody else keeps, out of this caller's view, is taken whatever
    // overwrite says, and is never replaced from behind.
    [Fact]
    public async Task AnIdTakenOutOfView_IsRefused_EvenWithOverwrite()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var refused = Error(await Union(Dani).CreateAsync("low/watch.json", LowWatch, true, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.AlreadyExists);
        _store.Stored(Household, "low")!.SubjectUserId.ShouldBe(Tradaly);
    }

    // A stat of an id somebody else keeps out of view says the id is there to be taken and nothing else:
    // what the union asks before a home watch is created under it.
    [Fact]
    public async Task AStatOfAnIdTakenOutOfView_SaysOnlyThatItIsTaken()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var hidden = Ok(await Mount(Dani).InfoAsync("/low", CancellationToken.None));
        var free = Ok(await Mount(Dani).InfoAsync("/high", CancellationToken.None));
        var hers = Ok(await Mount(Tradaly).InfoAsync("/low", CancellationToken.None));

        hidden.ShouldBe(new FsInfoResult { Exists = false, Path = "/low", Hidden = true });
        free.Hidden.ShouldBeFalse();
        free.Exists.ShouldBeFalse();
        hers.Exists.ShouldBeTrue();
        hers.Hidden.ShouldBeFalse();
    }

    // The converse of the case above: a home watch written by somebody who cannot see the glucose watch
    // holding its id would otherwise go to the home and leave the id held twice.
    [Fact]
    public async Task AHomeWatchUnderAnIdAGlucoseWatchHoldsOutOfView_IsRefused()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var refused = Error(await Union(Dani).CreateAsync(
            "low/watch.json", """{"triggers":[{"trigger":"state"}]}""", false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.AlreadyExists);
        refused.Message.ShouldNotContain("glucose");
        refused.Message.ShouldNotContain("Tradaly");
        _home.Files.Keys.ShouldNotContain("low/watch.json");
    }

    [Fact]
    public async Task AnIdInView_IsReplacedOnlyWithOverwrite()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        Error(await Union(Tradaly).CreateAsync("low/watch.json", LowWatch, false, true, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.AlreadyExists);
    }

    // Whose sensor a watch watches never moves because somebody else edits it.
    [Fact]
    public async Task HerWatch_EditedByTheGrantsHolder_StaysHers()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        var asHeReadsIt = await ReadAsync(Dethon, "low/watch.json");
        JsonNode.Parse(asHeReadsIt)!["triggers"]![0]!["person"]!.GetValue<string>().ShouldBe("tradaly");
        Ok(await Mount(Dethon).EditAsync("low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 65")], CancellationToken.None));

        var stored = _store.Stored(Household, "low")!;
        stored.SubjectUserId.ShouldBe(Tradaly);
        stored.WriterUserId.ShouldBe(Tradaly);
    }

    [Fact]
    public async Task AWatchOnHer_WrittenByTheGrantsHolder_SheEditsWithoutTheGrant()
    {
        await CreateAsync(Dethon, "her-low", LowFor("tradaly"));

        JsonNode.Parse(await ReadAsync(Tradaly, "her-low/watch.json"))!["triggers"]![0]!["person"].ShouldBeNull();
        Ok(await Mount(Tradaly).EditAsync("her-low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 75")], CancellationToken.None));

        _store.Stored(Household, "her-low")!.SubjectUserId.ShouldBe(Tradaly);
    }

    [Fact]
    public async Task AReplaceNamingNobody_KeepsTheWatchsSubject()
    {
        await CreateAsync(Tradaly, "low", LowWatch);

        Ok(await Mount(Dethon).CreateAsync("low/watch.json", LowWatch, true, true, CancellationToken.None));

        _store.Stored(Household, "low")!.SubjectUserId.ShouldBe(Tradaly);
    }

    // A one-shot that fired stays spent while written off, and a write turning it on arms it again.
    [Fact]
    public async Task ASpentOneShot_IsArmedAgainOnlyByAWriteTurningItOn()
    {
        await CreateAsync(Tradaly, "low", LowWatch);
        _store.Put(Household, _store.Stored(Household, "low")! with { Enabled = false, Spent = true, LastFiredAt = _now });

        Ok(await Mount(Tradaly).EditAsync("low/watch.json", [new TextEdit("Tell her she is low", "Tell her")], CancellationToken.None));
        _store.Stored(Household, "low")!.Spent.ShouldBeTrue();
        _store.Stored(Household, "low")!.LastFiredAt.ShouldBe(_now);

        Ok(await Mount(Tradaly).EditAsync("low/watch.json", [new TextEdit("\"enabled\": false", "\"enabled\": true")], CancellationToken.None));
        _store.Stored(Household, "low")!.Spent.ShouldBeFalse();
        _store.Stored(Household, "low")!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAnnouncementsTarget_IsCheckedAgainstTheHouseholdsSatellites()
    {
        await CreateAsync(Tradaly, "low", Announcing("""{"room":"kitchen"}"""));

        var refused = Error(await Mount(Tradaly).CreateAsync("nowhere/watch.json", Announcing("""{"room":"attic"}"""), false, true, CancellationToken.None));
        refused.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        refused.Message.ShouldContain("effects[0].target");
        refused.Message.ShouldContain("kitchen");
        refused.Message.ShouldContain("office");
        _store.Stored(Household, "nowhere").ShouldBeNull();

        Error(await Mount(Tradaly).CreateAsync("ghost/watch.json", Announcing("""{"satelliteId":"ghost-01"}"""), false, true, CancellationToken.None))
            .Message.ShouldContain("ghost-01");
    }

    [Fact]
    public async Task AnAnnouncementWithNoTarget_AsksNoVoiceServer()
    {
        await CreateAsync(Tradaly, "low", """
            {"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"announce","text":"Glucose is low"}]}
            """);

        _voices.Verify(v => v.For(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AVoiceServerNotAnswering_RefusesATargetedAnnouncement_SayingToLeaveTheTargetOut()
    {
        _satellites.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VoiceHubUnavailableException("connection refused"));

        var refused = Error(await Mount(Tradaly).CreateAsync("low/watch.json", Announcing("""{"room":"kitchen"}"""), false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        refused.Hint.ShouldNotBeNull().ShouldContain("leave target out");
        _store.Stored(Household, "low").ShouldBeNull();
    }

    private static string Announcing(string target) =>
        $$"""
          {"name":"Low","triggers":[{"trigger":"glucose","below":70}],
           "effects":[{"kind":"announce","text":"Glucose is low","target":{{target}}}]}
          """;

    // The agent signs a delegation for the write a person's turn makes, and the watch keeps the one its
    // last write carried: that is who its prompts run as.
    [Fact]
    public async Task AWrite_KeepsTheDelegationItCarried_AndAWriteCarryingNoneClearsIt()
    {
        var token = DelegationOf(Tradaly, "low", LowWatch);
        Ok(await Mount(Tradaly, token).CreateAsync("low/watch.json", LowWatch, false, true, CancellationToken.None));
        _store.Stored(Household, "low")!.Delegation.ShouldBe(token);

        Ok(await Mount(Tradaly).EditAsync("low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 65")], CancellationToken.None));

        _store.Stored(Household, "low")!.Delegation.ShouldBeNull();
    }

    // A server keeps a delegation only for the object and the person it names: one for another watch,
    // or for somebody other than the writer, would fire for nobody anyway, and kept it would hide that.
    [Fact]
    public async Task ADelegationNamingAnotherWatchOrAnotherPerson_IsNotKept()
    {
        Ok(await Mount(Tradaly, DelegationOf(Tradaly, "other", LowWatch))
            .CreateAsync("low/watch.json", LowWatch, false, true, CancellationToken.None));
        _store.Stored(Household, "low")!.Delegation.ShouldBeNull();

        Ok(await Mount(Tradaly, DelegationOf(Dani, "low", LowWatch))
            .CreateAsync("low/watch.json", LowWatch, true, true, CancellationToken.None));
        _store.Stored(Household, "low")!.Delegation.ShouldBeNull();
    }

    // Who a fire runs as is the person writing it or the person it watches, and nobody else: a watch
    // the agent fires for one person must not be made to say it is for a third.
    [Fact]
    public async Task UserId_NamingNeitherTheWriterNorTheSubject_IsRefusedNamingTheField()
    {
        var error = Error(await Union(Dethon).CreateAsync("low/watch.json",
            LowFor("tradaly").Replace("\"effects\"", $"\"userId\":\"{Dani}\",\"effects\""), false, true, CancellationToken.None));

        error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        error.Message.ShouldContain("userId");
        _store.Stored(Household, "low").ShouldBeNull();
    }

    [Theory]
    [InlineData(Dethon)]
    [InlineData(Tradaly)]
    public async Task UserId_MayBeTheWriterOrTheSubject(string userId)
    {
        Ok(await Union(Dethon).CreateAsync("low/watch.json",
            LowFor("tradaly").Replace("\"effects\"", $"\"userId\":\"{userId}\",\"effects\""), false, true, CancellationToken.None));

        _store.Stored(Household, "low")!.UserId.ShouldBe(userId);
    }

    // The file reads back with its userId, so the subject editing a watch somebody else set up on her
    // writes that person's id back: what the watch already said is not refused.
    [Fact]
    public async Task AnEdit_KeepingTheWatchsUserId_IsNotRefused()
    {
        await CreateAsync(Dethon, "her-low", LowFor("tradaly"));

        Ok(await Mount(Tradaly).EditAsync("her-low/watch.json", [new TextEdit("\"below\": 70", "\"below\": 75")], CancellationToken.None));

        _store.Stored(Household, "her-low")!.UserId.ShouldBe(Dethon);
    }

    private static string DelegationOf(string person, string id, string content) =>
        TestDelegations.For(new Account(Household, person), "signalr",
            GlucoseWatchDelegation.OfWrite($"{id}/watch.json", content, null, "jonas", new ReplyTarget("signalr", "conversation-1"))!);

    private async Task<IReadOnlyList<string>> Ids(string reader) =>
        Ok(await Mount(reader).GlobAsync("", "*/", CancellationToken.None)).Entries.Select(e => e.TrimEnd('/')).ToList();

    private async Task CreateAsync(string writer, string id, string content) =>
        Ok(await Union(writer).CreateAsync($"{id}/watch.json", content, false, true, CancellationToken.None));

    private async Task<string> ReadAsync(string reader, string path)
    {
        var chunks = new List<byte>();
        await foreach (var chunk in Mount(reader).ReadChunksAsync(path, CancellationToken.None))
        {
            chunks.AddRange(chunk.ToArray());
        }

        return System.Text.Encoding.UTF8.GetString(chunks.ToArray());
    }

    private UnionFileSystem Union(string? caller) => new(
    [
        new UnionFileSystem.Member("ha-watches", new FileSystemUnion(Watchers.RouteBy, [], Fallback: true), _home),
        new UnionFileSystem.Member(SugaryWatchFileSystem.Name, new FileSystemUnion(Watchers.RouteBy, [.. GlucoseTrigger.Kinds]),
            caller is null
                ? new SugaryWatchFileSystem(_store, _voices.Object, TimeZoneInfo.Utc, _time).For(FileSystemCaller.Nobody)
                : Mount(caller))
    ]);

    private FileSystemBackendBase Mount(string reader, string? delegation = null)
    {
        var household = new Mock<ICallHousehold>();
        household.SetupGet(h => h.Id).Returns(Household);
        household.Setup(h => h.SettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdSettings { Name = "Casa", TimeZone = "UTC" });
        household.Setup(h => h.SugaryPeopleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _people);
        var mount = new SugaryWatchFileSystem(_store, _voices.Object, TimeZoneInfo.Utc, _time);
        return mount.For(new FileSystemCaller(
            new ConversationContext("jonas", "conversation-1", reader, new ReplyTarget("signalr", "conversation-1"),
                Principal: new Principal("signalr", reader, new Account(Household, reader))),
            null, delegation, household.Object));
    }

    private static T Ok<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Ok>(result is FsResult<T>.Err e ? e.Error.Message : null).Value;

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Err>().Error;
}