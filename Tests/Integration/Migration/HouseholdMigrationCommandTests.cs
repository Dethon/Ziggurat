using System.Text.Json;
using System.Text.RegularExpressions;
using Accounts.Configuration;
using Accounts.Store;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Domain.Metrics;
using Infrastructure.Clients.Accounts;
using Infrastructure.Memory;
using Infrastructure.StateManagers;
using McpServerSugary.Store;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;
using Tests.Unit.Deployment;

namespace Tests.Integration.Migration;

// The household migration run against seeded stores — the accounts service, Redis, a Sugary log and a
// voices folder — as the operator runs it once on deploy, and again after anything went wrong.
public class HouseholdMigrationCommandTests(HouseholdMigrationRedisFixture redis)
    : IClassFixture<HouseholdMigrationRedisFixture>
{
    private const int Dimension = 4;
    private const string JackToken = "1001:jack-token-of-the-home";
    private const string JonasToken = "1002:jonas-token-of-the-home";

    private const string IdealistaKey = "idealista-key-of-the-home";
    private const string IdealistaSecret = "idealista-secret-of-the-home";

    // What the operator pipes into the run from the deployment's .env, which held them as BOTS__0,
    // BOTS__1 and IDEALISTA__*.
    private const string HomeKeys = $$"""
        {
          "telegramBots": { "jack": { "token": "{{JackToken}}" }, "jonas": { "token": "{{JonasToken}}" } },
          "idealista": { "apiKey": "{{IdealistaKey}}", "secret": "{{IdealistaSecret}}" }
        }
        """;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];

    private long _telegram = Random.Shared.NextInt64(1_000_000, 9_000_000);

    [Fact]
    public async Task ThePeople_BecomeAccountsLinkedToTheirTelegramAndEachIsPrintedAPasskeyLink()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        franAccount.TenantId.ShouldBe(anaAccount.TenantId);
        (await rig.MeAsync(franAccount))["roles"]!.AsArray().Select(r => r!.GetValue<string>())
            .ShouldBe(["owner", "operator"], ignoreOrder: true);
        (await rig.MeAsync(anaAccount))["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["member"]);
        output.Split('\n').Count(l => l.Contains($"{rig.Accounts.Address}accounts/enroll/", StringComparison.Ordinal))
            .ShouldBe(2, output);
    }

    // The chat client's retired list held each person's picture, which the file carries over.
    [Fact]
    public async Task APersonsAvatarInTheFile_IsTheirAccountsPicture()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await rig.RunAsync(
            new[]
            {
                new { name = fran.Name, telegramId = fran.TelegramId, avatarUrl = (string?)"/avatars/kakera.png" },
                new { name = ana.Name, telegramId = ana.TelegramId, avatarUrl = (string?)null }
            },
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", ana.Name]);

        exit.ShouldBe(0, output);
        (await rig.MeAsync(await rig.AccountOfAsync(fran.TelegramId)))["avatarUrl"]!.GetValue<string>()
            .ShouldBe("/avatars/kakera.png");
        (await rig.MeAsync(await rig.AccountOfAsync(ana.TelegramId)))["avatarUrl"].ShouldBeNull();
    }

    [Fact]
    public async Task EachPersonsMemoriesFromEveryName_AreOneMemoryUnderTheirAccount_AndTheHouseholdsAreTheOwners()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var memories = Memories();
        await memories.StoreAsync(Memory(fran.Telegram, "Likes game meat", [1, 0, 0, 0]));
        await memories.StoreAsync(Memory(fran.WebChat, "Works on Ziggurat", [0, 1, 0, 0]));
        await memories.StoreAsync(Memory(fran.Voice, "Likes game meat", [1, 0, 0, 0]));
        await memories.StoreAsync(Memory($"Frank-{_tag}", "Plays the cello", [0, 0, 1, 0]));
        await memories.StoreAsync(Memory(ana.WebChat, "Counts carbs", [0, 0, 0, 1]));
        var household = await memories.StoreAsync(Memory("household", $"The cat is called Mus {_tag}", [1, 1, 0, 0]));
        await memories.SaveProfileAsync(new PersonalityProfile
        {
            UserId = fran.Telegram, Summary = "Direct", LastUpdated = DateTimeOffset.UtcNow
        });

        var (exit, output) = await rig.RunAsync(
            new[] { Entry(fran, $"Frank-{_tag}"), Entry(ana) },
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", ana.Name]);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var franContents = (await memories.GetByUserIdAsync(franAccount.UserId)).Select(m => m.Content).ToList();
        franContents.Where(c => !c.StartsWith("The cat", StringComparison.Ordinal))
            .ShouldBe(["Likes game meat", "Works on Ziggurat", "Plays the cello"], ignoreOrder: true);
        franContents.ShouldContain(household.Content);
        var recalled = await memories.SearchAsync(franAccount.UserId, queryEmbedding: [0, 0, 1, 0], limit: 1);
        recalled.Single().Memory.Content.ShouldBe("Plays the cello");
        (await memories.GetByUserIdAsync((await rig.AccountOfAsync(ana.TelegramId)).UserId))
            .Select(m => m.Content).ShouldBe(["Counts carbs"]);
        (await memories.GetProfileAsync(franAccount.UserId))!.Summary.ShouldBe("Direct");
        foreach (var old in new[] { fran.Telegram, fran.WebChat, fran.Voice, $"Frank-{_tag}", ana.WebChat })
        {
            (await memories.GetByUserIdAsync(old)).ShouldBeEmpty();
        }

        (await memories.GetByIdAsync("household", household.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task ARememberedLoginSealedUnderTheMapsName_IsResealedUnderTheAccountAndOpensThere()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var logins = new RedisPersonStateStore(redis.Connection, MigrationRig.StateKey);
        var remembered = new PersonState(
            """{"cookies":[{"name":"sid","value":"abc","domain":".github.com"}],"origins":[]}""",
            [new SiteLogin("github.com", ["github.com"], DateTimeOffset.UnixEpoch, null)]);
        await logins.SaveAsync(fran.Name, remembered);

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var resealed = await logins.LoadAsync((await rig.AccountOfAsync(fran.TelegramId)).UserId);
        resealed.ShouldNotBeNull();
        resealed.StorageState.ShouldBe(remembered.StorageState);
        resealed.Logins.Single().Site.ShouldBe("github.com");
        (await logins.LoadAsync(fran.Name)).ShouldBeNull();
    }

    [Fact]
    public async Task APhoneSubscribedUnderTheChatClientsName_IsTheAccountsPhoneInTheSameSpace()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var push = new RedisPushSubscriptionStore(redis.Connection);
        var phone = new PushSubscriptionDto($"https://push.example/{_tag}", "p256dh", "auth");
        await BeforeHouseholds.PhoneAsync(redis.Connection.GetDatabase(), fran.WebChat, phone, $"kitchen-{_tag}");

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        (await push.GetBySpaceAsync(franAccount.TenantId, $"kitchen-{_tag}")).ShouldBe([(franAccount.UserId, phone)]);
        (await push.GetAllAsync()).ShouldNotContain(s => s.UserId == fran.WebChat);
    }

    // A space is the household's now, and so are the phones that hear of it: a phone already under
    // its person's account, in a space every household shared, joins the home household's space.
    [Fact]
    public async Task ASpacesPhones_AreTheHomeHouseholdsSpacesPhones()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var push = new RedisPushSubscriptionStore(redis.Connection);
        var phone = new PushSubscriptionDto($"https://push.example/space-{_tag}", "p256dh", "auth");
        var elsewhere = new Account($"elsewhere{_tag}", $"laura{_tag}");
        var hers = new PushSubscriptionDto($"https://push.example/elsewhere-{_tag}", "p256dh", "auth");
        await push.SaveAsync(elsewhere, hers, $"porch-{_tag}");

        var (first, _) = await RunAsync(rig, fran, ana);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        await BeforeHouseholds.PhoneAsync(db, franAccount.UserId, phone, $"porch-{_tag}");
        var (exit, output) = await RunAsync(rig, fran, ana);

        first.ShouldBe(0);
        exit.ShouldBe(0, output);
        (await push.GetBySpaceAsync(franAccount.TenantId, $"porch-{_tag}")).ShouldBe([(franAccount.UserId, phone)]);
        (await push.GetBySpaceAsync(elsewhere.TenantId, $"porch-{_tag}")).ShouldBe([(elsewhere.UserId, hers)]);
        (await db.KeyExistsAsync($"push:space:porch-{_tag}")).ShouldBeFalse();
    }

    [Fact]
    public async Task ASchedule_RunsAsTheAccountOfWhoeverWroteIt_OrAsTheOwnerWhereThatWasNobody()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var byTelegram = await BeforeHouseholds.ScheduleAsync(db, Schedule(new Principal("telegram", fran.Telegram)));
        var byChatClient = await BeforeHouseholds.ScheduleAsync(db, Schedule(new Principal("signalr", ana.WebChat.ToUpperInvariant())));
        var byGuest = await BeforeHouseholds.ScheduleAsync(db, Schedule(new Principal("voice", "household")));
        var fromBefore = await BeforeHouseholds.ScheduleAsync(db, Schedule(null));

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var home = franAccount.TenantId;
        var schedules = new RedisScheduleStore(redis.Connection);
        (await schedules.GetAsync(home, byTelegram.Id))!.CreatedBy.ShouldBe(new Principal("telegram", fran.Telegram, franAccount));
        (await schedules.GetAsync(home, byChatClient.Id))!.CreatedBy.ShouldBe(new Principal("signalr", anaAccount.UserId, anaAccount));
        (await schedules.GetAsync(home, byGuest.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        (await schedules.GetAsync(home, fromBefore.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        (await schedules.GetAsync(home, fromBefore.Id))!.Prompt.ShouldBe(fromBefore.Prompt);
    }

    // A schedule from before principals were recorded names nobody, but the conversation that wrote it
    // does: the write's tool result sits in a topic after its writer's own message. It runs as them —
    // given to the owner it would fire as the owner, into the owner's private topic, and the person who
    // asked for it would never see it again. A later write by somebody else wins, as the schedule runs
    // as whoever last wrote it.
    [Fact]
    public async Task ASchedule_WhoseWriterWasNeverRecorded_RunsAsThePersonWhoseConversationWroteIt()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        var anas = await BeforeHouseholds.ScheduleAsync(db, Schedule(null));
        var rewritten = await BeforeHouseholds.ScheduleAsync(db, Schedule(null));
        await ConversationWritingAsync(threads, ana.WebChat, DateTimeOffset.UtcNow.AddDays(-2), (anas.Id, "created"), (rewritten.Id, "created"));
        await ConversationWritingAsync(threads, fran.WebChat, DateTimeOffset.UtcNow.AddDays(-1), (rewritten.Id, "edited"));
        var neverWritten = await BeforeHouseholds.ScheduleAsync(db, Schedule(null));

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var schedules = new RedisScheduleStore(redis.Connection);
        var home = franAccount.TenantId;
        (await schedules.GetAsync(home, anas.Id))!.CreatedBy.ShouldBe(new Principal("signalr", anaAccount.UserId, anaAccount));
        (await schedules.GetAsync(home, rewritten.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        (await schedules.GetAsync(home, neverWritten.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        output.ShouldContain($"{neverWritten.Id}", customMessage: "a schedule given to the owner is named, so the operator can tell whose it was");
    }

    // A schedule's fire before the upgrade opened a topic of its own whose first message is the
    // scheduler's, which names nobody: given to the owner, a member's reminders became the owner's
    // topics. The fire's text is the schedule's prompt, and the schedule's writer is known, so the
    // topic is theirs; one no schedule's prompt matches stays the owner's.
    [Fact]
    public async Task ATopicAScheduleFiredBeforeOwners_IsTheSchedulesWriters()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        var water = await BeforeHouseholds.ScheduleAsync(db, Schedule(null) with { Prompt = $"Recuérdale a Ana que beba agua {_tag}" });
        await ConversationWritingAsync(threads, ana.WebChat, DateTimeOffset.UtcNow.AddDays(-1), (water.Id, "created"));
        var fired = await TopicAsync(threads, firstSender: ServiceSenders.Scheduler, text: null, firstText: water.Prompt);
        var firedForNothing = await TopicAsync(threads, firstSender: ServiceSenders.Scheduler, firstText: $"A schedule since deleted {_tag}");

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var home = franAccount.TenantId;
        (await threads.GetTopicAsync(home, fired.AgentId, fired.ChatId, fired.TopicId))!.OwnerId.ShouldBe(anaAccount.UserId);
        (await threads.GetTopicAsync(home, firedForNothing.AgentId, firedForNothing.ChatId, firedForNothing.TopicId))!
            .OwnerId.ShouldBe(franAccount.UserId);
    }

    // The deployment's schedules were all the home household's, so each is moved under it as it was —
    // still due when it was due — and nothing is left under the shape from before households.
    [Fact]
    public async Task ASchedule_IsKeptUnderTheHomeHousehold_AndStillComesDue()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var bins = await BeforeHouseholds.ScheduleAsync(db,
            Schedule(new Principal("telegram", fran.Telegram)) with { NextRunAt = DateTime.UtcNow.AddMinutes(-1) });
        var schedules = new RedisScheduleStore(redis.Connection);
        var elsewhere = await schedules.CreateAsync(Schedule(null) with { TenantId = $"elsewhere{_tag}" });

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var home = (await rig.AccountOfAsync(fran.TelegramId)).TenantId;
        (await schedules.ListAsync(home)).Where(s => s.Prompt.EndsWith(_tag, StringComparison.Ordinal))
            .Select(s => (s.Id, s.TenantId, s.Prompt)).ShouldBe([(bins.Id, home, bins.Prompt)]);
        (await schedules.GetDueSchedulesAsync(DateTime.UtcNow)).ShouldContain(s => s.Id == bins.Id && s.TenantId == home);
        (await db.KeyExistsAsync($"schedule:{bins.Id}")).ShouldBeFalse();
        (await db.SetContainsAsync("schedules", bins.Id)).ShouldBeFalse();
        (await schedules.GetAsync(elsewhere.TenantId, elsewhere.Id)).ShouldBe(elsewhere);
    }

    [Fact]
    public async Task ATopicFromBeforeOwners_IsSharedAndOwnedByWhoeverStartedIt_OrByTheOwner()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        var anasTopic = await TopicAsync(threads, firstSender: ana.WebChat);
        var guestsTopic = await TopicAsync(threads, firstSender: $"Salón-{_tag}");
        var someonesPrivate = await TopicAsync(threads, firstSender: ana.WebChat,
            owned: t => t with { OwnerId = "0123456789abcdef0123456789abcdef", TenantId = "home", OwnerName = "Bea" });
        var aGroupsCopy = await TopicAsync(threads, firstSender: fran.Telegram,
            owned: t => t with { TenantId = "home", Shared = true });

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var home = franAccount.TenantId;
        (await threads.GetTopicAsync(home, anasTopic.AgentId, anasTopic.ChatId, anasTopic.TopicId)).ShouldBe(anasTopic with
        {
            OwnerId = anaAccount.UserId,
            TenantId = anaAccount.TenantId,
            Shared = true,
            OwnerName = ana.Name
        });
        (await threads.GetTopicAsync(home, guestsTopic.AgentId, guestsTopic.ChatId, guestsTopic.TopicId)).ShouldBe(guestsTopic with
        {
            OwnerId = franAccount.UserId,
            TenantId = franAccount.TenantId,
            Shared = true
        });
        (await threads.GetTopicAsync("home", someonesPrivate.AgentId, someonesPrivate.ChatId, someonesPrivate.TopicId))
            .ShouldBe(someonesPrivate);
        (await threads.GetTopicAsync("home", aGroupsCopy.AgentId, aGroupsCopy.ChatId, aGroupsCopy.TopicId)).ShouldBe(aGroupsCopy);
    }

    // The deployment's topics were all the home household's, so each is moved under it as it was: still
    // listed in its space, still found by what was said in it, its conversation still leading to it —
    // and the search index from before households, which had no household to filter on, is gone.
    // Another household's topic is nothing this run touches.
    // The topic-index migration retired in August left its once-only marker under the index prefix,
    // a string where every index is a space's ordering: prod's run died on it in the dry run.
    [Fact]
    public async Task TheRetiredIndexMigrationsMarker_IsNoSpacesOrdering_AndIsLeftAsItWas()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        await db.StringSetAsync("topics:migrated", "2026-08-13T17:29:06.9846127+00:00");
        try
        {
            var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
            await TopicAsync(threads, firstSender: ana.WebChat, text: $"marker{_tag}");

            var (dryExit, dryOutput) = await RunAsync(rig, fran, ana, input: null, "--dry-run");
            var (exit, output) = await RunAsync(rig, fran, ana);

            dryExit.ShouldBe(0, dryOutput);
            exit.ShouldBe(0, output);
            (await db.StringGetAsync("topics:migrated")).ToString().ShouldBe("2026-08-13T17:29:06.9846127+00:00");
        }
        finally
        {
            await db.KeyDeleteAsync("topics:migrated");
        }
    }

    [Fact]
    public async Task ATopic_IsListedAndSearchedInTheHomeHousehold_AndItsConversationStillLeadsToIt()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var db = redis.Connection.GetDatabase();
        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        await BeforeHouseholds.SearchIndexAsync(db);
        var quince = await TopicAsync(threads, firstSender: ana.WebChat, text: $"quince{_tag} jelly");
        var elsewhere = new TopicMetadata(Guid.NewGuid().ToString("N"), Random.Shared.NextInt64(1, long.MaxValue), 1,
            "jonas", $"Topic {_tag}", DateTimeOffset.UtcNow, null, TenantId: $"elsewhere{_tag}", Shared: true);
        await threads.SaveTopicAsync(elsewhere);

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var franSees = new TopicViewer(franAccount.UserId, franAccount.TenantId);
        var held = (await threads.GetTopicByChatIdAndThreadIdAsync(quince.AgentId, quince.ChatId, quince.ThreadId))
            .ShouldNotBeNull();
        held.TenantId.ShouldBe(franAccount.TenantId);
        IReadOnlyList<TopicMetadata> found = [];
        await Eventually.Until(
            async () => (found = (await threads.SearchTopicsAsync("jonas", "default", franSees, $"quince{_tag}", null, 10)).Topics)
                .Count > 0,
            "the moved topic to be indexed");
        found.Select(t => t.TopicId).ShouldBe([quince.TopicId]);
        (await threads.GetTopicPageAsync("jonas", "default", franSees, null, 500)).Topics
            .ShouldContain(t => t.TopicId == quince.TopicId);
        (await db.KeyExistsAsync($"topic:jonas:{quince.ChatId}:{quince.TopicId}")).ShouldBeFalse();
        (await db.KeyExistsAsync($"topicdoc:jonas:{quince.ChatId}:{quince.TopicId}")).ShouldBeFalse();
        ((RedisResult[])(await db.ExecuteAsync("FT._LIST"))!).Select(i => i.ToString()).ShouldNotContain("idx:topics");
        (await threads.GetTopicAsync(elsewhere.TenantId!, "jonas", elsewhere.ChatId, elsewhere.TopicId)).ShouldBe(elsewhere);
    }

    [Fact]
    public async Task ASpeakerProfile_IsNamedAfterItsPersonsAccount()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var enrolled = Path.Combine(rig.Voices, fran.Voice);
        Directory.CreateDirectory(enrolled);
        await File.WriteAllBytesAsync(Path.Combine(enrolled, "take-1.wav"), [1, 2, 3]);

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franId = (await rig.AccountOfAsync(fran.TelegramId)).UserId;
        (await File.ReadAllBytesAsync(Path.Combine(rig.Voices, franId, "take-1.wav"))).ShouldBe([1, 2, 3]);
        Directory.Exists(enrolled).ShouldBeFalse();
    }

    [Fact]
    public async Task TheSugaryLogFromBefore_IsTheNamedPersonsAndItsKitchenTheHouseholds()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await using (var db = await rig.Sugary.CreateDbContextAsync())
        {
            db.Meals.Add(new Meal
            {
                UserId = Owners.Unassigned, TenantId = Owners.Unassigned, Id = Guid.NewGuid(),
                EatenAt = DateTimeOffset.UtcNow, Carbs = 42
            });
            db.Foods.Add(new Food
            {
                TenantId = Owners.Unassigned, Id = Guid.NewGuid(), Name = "Pan de molde", CarbsPer100g = 46,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        await using var hers = rig.SugaryOf(anaAccount);
        (await hers.Meals.Select(m => m.Carbs).ToListAsync()).ShouldBe([42]);
        await using var his = rig.SugaryOf(franAccount);
        (await his.Meals.CountAsync()).ShouldBe(0);
        (await his.Foods.Select(f => f.Name).ToListAsync()).ShouldBe(["Pan de molde"]);
    }

    // The collector keys by household from the deploy on, so the day of the deploy is in both shapes
    // when the run comes: what the dashboard counted before is the home's, added to what it counted since.
    [Fact]
    public async Task TheRollUpsFromBeforeHouseholds_AreTheHomeHouseholds_AddedToWhatItCountedSince()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        (await RunAsync(rig, fran, ana)).Exit.ShouldBe(0);
        var home = (await rig.AccountOfAsync(fran.TelegramId)).TenantId;
        var day = Day();
        var db = redis.Connection.GetDatabase();
        var collector = new MetricsCollectorService(
            redis.Connection, Mock.Of<IHubContext<MetricsHub>>(h => h.Clients.All == Mock.Of<IClientProxy>()),
            NullLogger<MetricsCollectorService>.Instance);
        await SeedLegacyRollUpsAsync(day, input: 100, error: $"Vault gone {_tag}");
        await collector.ProcessEventAsync(
            new TokenUsageEvent
            {
                Sender = "fran", Model = "z-ai/glm-5", InputTokens = 20, OutputTokens = 0, Cost = 0m,
                TenantId = home, Timestamp = Noon(day)
            },
            db);

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        output.ShouldContain("Metric roll-ups");
        var query = new MetricsQueryService(redis.Connection);
        (await query.GetSummaryAsync(day, day, home)).InputTokens.ShouldBe(120);
        (await query.GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, day, day, home))
            .Select(e => e.InputTokens).ShouldBe([100, 20], ignoreOrder: true);
        (await query.GetRecentErrorsAsync(household: home)).ShouldContain(e => e.Message == $"Vault gone {_tag}");
        (await query.GetHouseholdsAsync()).ShouldContain(home);
        (await db.KeyExistsAsync(LegacyKey("totals", day))).ShouldBeFalse();
        (await db.KeyTimeToLiveAsync(MetricKeys.Daily(MetricKeys.Tokens, home, day))).ShouldNotBeNull();
    }

    // The chat client's spaces were a list in its own settings, the same for everybody; they are the
    // home household's configuration now, and each opens on whatever agent was picked in it.
    [Fact]
    public async Task TheHomeHouseholdsSpaces_AreTheChatClientsList()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var home = (await rig.AccountOfAsync(fran.TelegramId)).TenantId;
        var spaces = await rig.Accounts.Service<HouseholdConfigStore>().ReadAsync(home, HouseholdSections.SpacesSchema, CancellationToken.None);
        spaces.ShouldNotBeNull().Spaces.Select(s => (s.Slug, s.Name, s.AccentColor, s.DefaultAgent, s.Order))
            .ShouldBe([("default", "Main", "#e9601f", (string?)null, 0), ("x", "X", "#e3c727", (string?)null, 1)]);
    }

    // Spaces an owner saved before the migration ran are theirs, and the list is not put over them.
    [Fact]
    public async Task AHomeHouseholdThatSavedItsSpaces_KeepsThem()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var store = rig.Accounts.Service<HouseholdConfigStore>();
        var home = await rig.HomeHouseholdAsync();
        var kitchen = new SpacesSection { Spaces = [HouseholdSpace.Default with { Slug = "kitchen", Name = "Kitchen" }] };
        (await store.SaveAsync(home, HouseholdSections.SpacesSchema, HouseholdSections.SpacesSchema.Write(kitchen), CancellationToken.None))
            .ShouldBeOfType<SectionSave.Saved>();

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        (await store.ReadAsync(home, HouseholdSections.SpacesSchema, CancellationToken.None)).ShouldNotBeNull().Spaces.Select(s => s.Slug)
            .ShouldBe(["kitchen"]);
        output.ShouldContain("already saved");
    }

    // The home's agents were the agent's settings' four, the same for every turn: they are its own
    // configuration now, seeded whole, its days read in the deployment's zone, and every feature its
    // agents used on — Idealista too, which a new household starts without.
    [Fact]
    public async Task TheHomeHousehold_IsGivenTheDeploymentsZone_TodaysAgents_AndEveryFeature()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var home = (await rig.AccountOfAsync(fran.TelegramId)).TenantId;
        var store = rig.Accounts.Service<HouseholdConfigStore>();
        var settings = (await store.ReadAsync(home, HouseholdSections.SettingsSchema, CancellationToken.None)).ShouldNotBeNull();
        settings.TimeZone.ShouldBe(TimeZoneInfo.Local.Id);
        settings.Language.ShouldBeNull();
        var agents = (await store.ReadAsync(home, HouseholdSections.AgentsSchema, CancellationToken.None)).ShouldNotBeNull();
        agents.DefaultAgent.ShouldBe("jonas");
        agents.Agents.Select(a => a.Id).ShouldBe(["jack", "jonas", "nabu", "sugary"]);
        HouseholdSections.AgentsSchema.Write(agents).ToJsonString()
            .ShouldBe(HouseholdSections.AgentsSchema.Write(HomeAgents.Section()).ToJsonString());
        (await store.ReadAsync(home, HouseholdSections.FeaturesSchema, CancellationToken.None))
            .ShouldBe(new FeaturesSection { Idealista = true });
        output.ShouldContain("jack, jonas, nabu, sugary");
    }

    // Agents an owner or the operator saved before the migration ran are theirs.
    [Fact]
    public async Task AHomeHouseholdThatSavedItsAgents_KeepsThem()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var store = rig.Accounts.Service<HouseholdConfigStore>();
        var home = await rig.HomeHouseholdAsync();
        var own = new AgentsSection { Agents = [new HouseholdAgent { Id = "ana", Template = "jonas", Name = "Ana" }] };
        (await store.SaveAsync(home, HouseholdSections.AgentsSchema, HouseholdSections.AgentsSchema.Write(own), CancellationToken.None))
            .ShouldBeOfType<SectionSave.Saved>();

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        (await store.ReadAsync(home, HouseholdSections.AgentsSchema, CancellationToken.None)).ShouldNotBeNull().Agents
            .Select(a => a.Id).ShouldBe(["ana"]);
    }

    // The jack and jonas bots and Idealista's keys were the deployment's .env; they are the home's own
    // keys now, each sealed like any secret and answered to its own server alone: the bots, each
    // bound to the agent it always answered as, to Telegram, and Idealista's to Idealista on a call
    // made for one of the household's people. They arrive on the run's input, never on its command
    // line or in a file of the volume.
    [Fact]
    public async Task TheHomesKeys_AreSealed_AndEachAnsweredToItsOwnServer()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var home = franAccount.TenantId;
        await using var telegram = rig.Reader(ConfigurationReader.Telegram);
        await using var idealista = rig.Reader(ConfigurationReader.Idealista);
        (await telegram.TelegramBotsAsync(CancellationToken.None)).Where(b => b.HouseholdId == home)
            .ShouldBe([new HouseholdTelegramBot(home, "jack", JackToken), new HouseholdTelegramBot(home, "jonas", JonasToken)],
                ignoreOrder: true);
        (await idealista.IdealistaKeysAsync(home, MigrationRig.IdealistaCall(franAccount), CancellationToken.None))
            .ShouldBe(new IdealistaKeys(IdealistaKey, IdealistaSecret));
        await using (var db = await rig.Accounts.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            var stored = (await db.HouseholdSections.SingleAsync(s => s.TenantId == home && s.Section == HouseholdSections.Keys)).Document;
            new[] { JackToken, JonasToken, IdealistaKey, IdealistaSecret }.ShouldAllBe(secret => !stored.Contains(secret));
        }

        output.ShouldContain("jack, jonas");
        output.ShouldContain("Idealista");
        new[] { JackToken, JonasToken, IdealistaKey, IdealistaSecret }.ShouldAllBe(secret => !output.Contains(secret));
    }

    // The operator may have saved the home's Idealista keys before the run, the deployment's having
    // left Idealista's server on the deploy: those are kept, and the bots are seeded beside them.
    [Fact]
    public async Task AnIdealistaKeySavedBeforeTheRun_IsKept_AndTheBotsAreSeededBesideIt()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var store = rig.Accounts.Service<HouseholdConfigStore>();
        var home = await rig.HomeHouseholdAsync();
        var own = new KeysSection { Idealista = new IdealistaKeys("idealista-set-by-hand", "secret-set-by-hand") };
        (await store.SaveAsync(home, HouseholdSections.KeysSchema, HouseholdSections.KeysSchema.Write(own), CancellationToken.None))
            .ShouldBeOfType<SectionSave.Saved>();

        var (exit, output) = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");

        exit.ShouldBe(0, output);
        var keys = (await store.ReadAsync(home, HouseholdSections.KeysSchema, CancellationToken.None)).ShouldNotBeNull();
        keys.Idealista.ShouldBe(own.Idealista);
        keys.TelegramBots.Select(b => (b.Key, b.Value.Token)).ShouldBe([("jack", JackToken), ("jonas", JonasToken)], ignoreOrder: true);
        output.ShouldContain("Idealista's keys the household already held are kept");
    }

    // A run given no keys seeds none and says how, so the steps before and after still run.
    [Fact]
    public async Task ARunGivenNoKeys_SeedsNone_AndSaysHow()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        output.ShouldContain("--home-keys");
        await using var db = await rig.Accounts.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.HouseholdSections.AnyAsync(s => s.Section == HouseholdSections.Keys)).ShouldBeFalse();
    }

    // A file that is not the home's keys — a bot of an agent the home does not have, half of
    // Idealista's pair, the OpenRouter key the home never had of its own — migrates nothing, and no
    // value is ever printed.
    [Theory]
    [InlineData("""{ "telegramBots": { "jack": "1001:jack-token" } }""")]
    [InlineData("""{ "telegramBots": { "Jack": { "token": "1001:jack-token" } } }""")]
    [InlineData("""{ "telegramBots": { "jak": { "token": "1001:jack-token" } } }""")]
    [InlineData("""{ "telegramBots": { "jack": { "token": "" } } }""")]
    [InlineData("""{ "jack": { "token": "1001:jack-token" } }""")]
    [InlineData("""{ "idealista": { "apiKey": "1001:jack-token" } }""")]
    [InlineData("""{ "idealista": { "apiKey": "1001:jack-token", "secret": " " } }""")]
    [InlineData("""{ "openRouter": { "apiKey": "1001:jack-token" } }""")]
    [InlineData("""{ }""")]
    [InlineData("not json 1001:jack-token")]
    public async Task AKeysFileThatIsNotTheHomesKeys_MigratesNothing(string file)
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());

        var (exit, output) = await RunAsync(rig, fran, ana, input: file, "--home-keys", "-");

        exit.ShouldBe(2, output);
        output.ShouldContain("nothing was migrated");
        output.ShouldNotContain("1001:jack-token");
        await using var db = await rig.Accounts.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.CountAsync()).ShouldBe(0);
    }

    // The whole migration over the home as the deployment held it: a dry run names a move under every
    // step, writes nothing anywhere, and counts exactly the changes the run then makes.
    [Fact]
    public async Task ADryRun_ReportsEveryStepsMoves_AndWritesNothingAnywhere()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedTheHomeAsync(rig, fran, ana);
        var before = await SnapshotAsync(rig);

        var (exit, output) = await RunAsync(rig, fran, ana, input: HomeKeys, "--dry-run", "--home-keys", "-");

        exit.ShouldBe(0, output);
        output.ShouldContain("dry run");
        output.ShouldNotContain("nothing to move");
        output.ShouldContain("telegramBots: jack, jonas");
        output.ShouldContain($"'{fran.Telegram}'");
        output.ShouldContain($"'{fran.Voice}' renamed");
        var wouldBe = Changes(output, "would be made");
        (await SnapshotAsync(rig)).ShouldBe(before);
        var run = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");
        run.Exit.ShouldBe(0, run.Output);
        Changes(run.Output, "made").ShouldBe(wouldBe, run.Output);
    }

    // After the run the home is what its people knew, read the way the channels read it now — by
    // household and by account: each person's topics in each space, listed and found by what was said,
    // the schedules still due, the phones in their spaces, and the configuration every reader is
    // answered, the chat client's spaces on a signed-in person's assertion.
    [Fact]
    public async Task AfterTheRun_ASignedInPersonSeesTheirTopicsSchedulesAndSpaces()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var before = await SeedTheHomeAsync(rig, fran, ana);

        var (exit, output) = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        var home = anaAccount.TenantId;
        await using var chatClient = rig.Reader(ConfigurationReader.ChatClient);
        (await chatClient.SpacesAsync(home, await rig.SignedInAsync(anaAccount), CancellationToken.None)).Spaces
            .Select(s => (s.Slug, s.Name)).ShouldBe([("default", "Main"), ("x", "X")]);

        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        var anaSees = new TopicViewer(anaAccount.UserId, home);
        (await threads.GetTopicPageAsync("jonas", "default", anaSees, null, 500)).Topics
            .ShouldContain(t => t.TopicId == before.Dinner.TopicId && t.OwnerId == anaAccount.UserId);
        (await threads.GetTopicPageAsync("jonas", "x", anaSees, null, 500)).Topics
            .ShouldContain(t => t.TopicId == before.Trip.TopicId && t.OwnerId == franAccount.UserId);
        IReadOnlyList<TopicMetadata> found = [];
        await Eventually.Until(
            async () => (found = (await threads.SearchTopicsAsync("jonas", "default", anaSees, $"quince{_tag}", null, 10)).Topics)
                .Count > 0,
            "the moved topic to be found by what was said in it");
        found.Select(t => t.TopicId).ShouldBe([before.Dinner.TopicId]);
        (await threads.GetTopicByChatIdAndThreadIdAsync("jonas", before.Dinner.ChatId, before.Dinner.ThreadId))
            .ShouldNotBeNull().TopicId.ShouldBe(before.Dinner.TopicId);

        var schedules = new RedisScheduleStore(redis.Connection);
        (await schedules.ListAsync(home)).Where(s => s.Prompt.EndsWith(_tag, StringComparison.Ordinal))
            .Select(s => (s.Id, s.CreatedBy?.Account))
            .ShouldBe([(before.Bins.Id, franAccount), (before.Nobodys.Id, franAccount)], ignoreOrder: true);
        (await schedules.GetDueSchedulesAsync(DateTime.UtcNow)).ShouldContain(s => s.Id == before.Bins.Id && s.TenantId == home);

        var push = new RedisPushSubscriptionStore(redis.Connection);
        (await push.GetBySpaceAsync(home, "default")).ShouldContain((anaAccount.UserId, before.AnasPhone));
        (await push.GetBySpaceAsync(home, "x")).ShouldContain((franAccount.UserId, before.FransPhone));

        await using var agent = rig.Reader(ConfigurationReader.Agent);
        await using var telegram = rig.Reader(ConfigurationReader.Telegram);
        await using var idealista = rig.Reader(ConfigurationReader.Idealista);
        var configured = await agent.ForAgentAsync(home, CancellationToken.None);
        configured.IsHome.ShouldBeTrue();
        configured.Agents.Agents.Select(a => a.Id).ShouldBe(["jack", "jonas", "nabu", "sugary"]);
        configured.Features.Idealista.ShouldBeTrue();
        (await telegram.TelegramBotsAsync(CancellationToken.None)).Where(b => b.HouseholdId == home).Select(b => b.AgentId)
            .ShouldBe(["jack", "jonas"], ignoreOrder: true);
        (await idealista.IdealistaKeysAsync(home, MigrationRig.IdealistaCall(anaAccount), CancellationToken.None))
            .ShouldBe(new IdealistaKeys(IdealistaKey, IdealistaSecret));
    }

    [Fact]
    public async Task RunningItAgain_ChangesNothingAndSaysSo()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedTheHomeAsync(rig, fran, ana);
        var first = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");
        first.Exit.ShouldBe(0, first.Output);
        var migrated = await SnapshotAsync(rig);

        var (exit, output) = await RunAsync(rig, fran, ana, input: HomeKeys, "--home-keys", "-");

        exit.ShouldBe(0, output);
        output.ShouldContain("Nothing changed");
        output.ShouldNotContain("/accounts/enroll/");
        (await SnapshotAsync(rig)).ShouldBe(migrated);
    }

    [Fact]
    public async Task ARunNamingSomebodyTheFileDoesNotList_MigratesNothing()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedTheHomeAsync(rig, fran, ana);
        var before = await SnapshotAsync(rig);

        var (exit, output) = await rig.RunAsync(
            new[] { Entry(fran), Entry(ana) },
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", $"Bea-{_tag}"]);

        exit.ShouldBe(2);
        output.ShouldContain($"Bea-{_tag} is not one of the people listed");
        (await SnapshotAsync(rig)).ShouldBe(before);
    }

    // The dashboard's lists as the collector kept them before households: one set for everybody.
    private async Task SeedLegacyRollUpsAsync(DateOnly day, int input, string error)
    {
        var db = redis.Connection.GetDatabase();
        var usage = new TokenUsageEvent
        {
            Sender = "fran", Model = "z-ai/glm-5", InputTokens = input, OutputTokens = 0, Cost = 0m, Timestamp = Noon(day)
        };
        await db.SortedSetAddAsync(
            LegacyKey("tokens", day), JsonSerializer.Serialize<MetricEvent>(usage, _metricJson),
            usage.Timestamp.ToUnixTimeMilliseconds());
        await db.KeyExpireAsync(LegacyKey("tokens", day), TimeSpan.FromDays(30));
        await db.HashIncrementAsync(LegacyKey("totals", day), "tokens:input", input);
        await db.KeyExpireAsync(LegacyKey("totals", day), TimeSpan.FromDays(30));
        await db.ListLeftPushAsync(
            MetricKeys.LegacyRecentErrors,
            JsonSerializer.Serialize<MetricEvent>(
                new ErrorEvent { Service = "agent", ErrorType = "Gone", Message = error, Timestamp = Noon(day) },
                _metricJson));
    }

    private static readonly JsonSerializerOptions _metricJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string LegacyKey(string family, DateOnly day) => $"metrics:{family}:{day:yyyy-MM-dd}";

    // A day of the class's own, so two tests' roll-ups never fall on one key.
    private static DateOnly Day() => new DateOnly(2026, 1, 1).AddDays(Random.Shared.Next(0, 3000));

    private static DateTimeOffset Noon(DateOnly day) => new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

    // The home as the deployment held it on the day of the deploy: every shared store in the shape from
    // before households — the topics and their search index, the spaces' phones, the schedules, the
    // dashboard's roll-ups — and what the people kept under the names the channels gave them: their
    // memories and the household's, a remembered login, a voice, the Sugary log. Two spaces, as the
    // chat client shipped them, each with a topic and a phone.
    private async Task<TheHome> SeedTheHomeAsync(MigrationRig rig, Person fran, Person ana)
    {
        var db = redis.Connection.GetDatabase();
        await SeedLegacyRollUpsAsync(Day(), input: 7, error: $"Printer jammed {_tag}");
        await Memories().StoreAsync(Memory(fran.Telegram, $"Likes game meat {_tag}", [1, 0, 0, 0]));
        await Memories().StoreAsync(Memory("household", $"The cat is called Mus {_tag}", [1, 1, 0, 0]));
        await new RedisPersonStateStore(redis.Connection, MigrationRig.StateKey).SaveAsync(fran.Name, new PersonState(
            """{"cookies":[{"name":"sid","value":"abc","domain":".github.com"}],"origins":[]}""",
            [new SiteLogin("github.com", ["github.com"], DateTimeOffset.UnixEpoch, null)]));
        var anasPhone = new PushSubscriptionDto($"https://push.example/ana-{_tag}", "p", "a");
        var fransPhone = new PushSubscriptionDto($"https://push.example/fran-{_tag}", "p", "a");
        await BeforeHouseholds.PhoneAsync(db, ana.WebChat, anasPhone);
        await BeforeHouseholds.PhoneAsync(db, fran.WebChat, fransPhone, "x");
        var bins = await BeforeHouseholds.ScheduleAsync(db,
            Schedule(new Principal("telegram", fran.Telegram)) with { NextRunAt = DateTime.UtcNow.AddMinutes(-1) });
        var nobodys = await BeforeHouseholds.ScheduleAsync(db, Schedule(null));
        await BeforeHouseholds.SearchIndexAsync(db);
        var threads = new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System);
        var dinner = await TopicAsync(threads, ana.WebChat, text: $"quince{_tag} jelly");
        var trip = await TopicAsync(threads, fran.Telegram, owned: t => t with { SpaceSlug = "x" });
        Directory.CreateDirectory(Path.Combine(rig.Voices, fran.Voice));
        await File.WriteAllBytesAsync(Path.Combine(rig.Voices, fran.Voice, "take-1.wav"), [1]);
        await using var sugary = await rig.Sugary.CreateDbContextAsync();
        sugary.Meals.Add(new Meal
        {
            UserId = Owners.Unassigned, TenantId = Owners.Unassigned, Id = Guid.NewGuid(), EatenAt = DateTimeOffset.UtcNow
        });
        await sugary.SaveChangesAsync();
        return new TheHome(bins, nobodys, dinner, trip, anasPhone, fransPhone);
    }

    private sealed record TheHome(
        Schedule Bins, Schedule Nobodys, TopicMetadata Dinner, TopicMetadata Trip, PushSubscriptionDto AnasPhone,
        PushSubscriptionDto FransPhone);

    // The count a run's last line gives: "12 changes made", "12 changes would be made".
    private static int Changes(string output, string made) =>
        int.Parse(Regex.Match(output, $@"(\d+) changes? {made}\.").Groups[1].Value);

    // Everything the run could touch, as it stands: every Redis key with its serialized value, the
    // accounts service's people and links, the voices folder and the owners of the Sugary log's rows.
    private async Task<string> SnapshotAsync(MigrationRig rig)
    {
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints()[0]);
        var db = redis.Connection.GetDatabase();
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(db.Database, "*"))
        {
            keys.Add(key.ToString());
        }

        var values = new List<string>();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            values.Add($"{key}={Convert.ToBase64String(await db.KeyDumpAsync(key) ?? [])}");
        }

        await using var accounts = await rig.Accounts
            .Service<IDbContextFactory<global::Accounts.Store.AccountsDbContext>>().CreateDbContextAsync();
        await using var sugary = await rig.Sugary.CreateDbContextAsync();
        return string.Join('\n', values.Concat(
        [
            $"people={await accounts.Users.CountAsync()}",
            $"links={await accounts.Links.CountAsync()}",
            $"identities={await accounts.LinkedIdentities.CountAsync()}",
            $"sections={string.Join(',', await accounts.HouseholdSections.OrderBy(s => s.Section).Select(s => s.Section + s.Document + s.UpdatedAt).ToListAsync())}",
            $"voices={string.Join(',', Directory.EnumerateFileSystemEntries(rig.Voices, "*", SearchOption.AllDirectories).Order())}",
            $"meals={string.Join(',', await sugary.Meals.IgnoreQueryFilters().Select(m => m.UserId).ToListAsync())}"
        ]));
    }

    private sealed record Person(string Name, long TelegramId, string Telegram, string WebChat, string Voice);

    private Person Fran() => new($"Fran-{_tag}", ++_telegram, $"dethon_{_tag}", $"Francisco-{_tag}", $"fran-{_tag}");

    private Person Ana() => new($"Ana-{_tag}", ++_telegram, $"ana_{_tag}", $"Ana-{_tag}", $"ana-{_tag}");

    private RedisStackMemoryStore Memories() => new(redis.Connection, TestEmbeddingOptions.At(Dimension));

    private static MemoryEntry Memory(string userId, string content, float[] embedding) => new()
    {
        Id = $"mem_{Guid.NewGuid():N}",
        UserId = userId,
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.8,
        Confidence = 0.9,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        Embedding = embedding
    };

    private Schedule Schedule(Principal? createdBy) => new()
    {
        Id = $"sched-{Guid.NewGuid():N}",
        AgentId = "jonas",
        Prompt = $"Remind me about the bins {_tag}",
        CronExpression = "0 21 * * 1",
        CreatedBy = createdBy,
        CreatedAt = DateTime.UtcNow,
        NextRunAt = DateTime.UtcNow.AddDays(1)
    };

    // A topic whose conversation began with a message the chat client stamped with whatever name its
    // picker sent, as every message did before accounts.
    private async Task<TopicMetadata> TopicAsync(
        RedisThreadStateStore threads, string firstSender, Func<TopicMetadata, TopicMetadata>? owned = null,
        string? text = null, string firstText = "What's for dinner?")
    {
        var now = DateTimeOffset.UtcNow;
        var topic = new TopicMetadata(
            Guid.NewGuid().ToString("N"), Random.Shared.NextInt64(1, long.MaxValue), Random.Shared.NextInt64(1, 1_000_000),
            "jonas", $"Topic {_tag}", now, now, "default");
        topic = owned?.Invoke(topic) ?? topic;
        await BeforeHouseholds.TopicAsync(redis.Connection.GetDatabase(), topic, text);
        var first = new ChatMessage(ChatRole.User, firstText);
        first.SetSenderId(firstSender);
        await threads.AppendMessagesAsync(
            new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString(),
            [first, new ChatMessage(ChatRole.Assistant, "Pasta.")]);
        return topic;
    }

    // A conversation as a chat client person had it before accounts: their message, then the agent's
    // writes to /schedules, each answered by the schedule mount's own result, one second apart.
    private async Task ConversationWritingAsync(
        RedisThreadStateStore threads, string sender, DateTimeOffset at, params (string ScheduleId, string Status)[] writes)
    {
        var topic = await TopicAsync(threads, firstSender: sender);
        var key = new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString();
        var asked = new ChatMessage(ChatRole.User, "Remind me every Monday");
        asked.SetSenderId(sender);
        var messages = new List<ChatMessage> { asked };
        foreach (var (id, status) in writes)
        {
            var path = $"/schedules/jonas/{id}/schedule.json";
            var callId = Guid.NewGuid().ToString("N");
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, "domain__filesystem__file_create", new Dictionary<string, object?> { ["filePath"] = path })]));
            var result = new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, JsonSerializer.SerializeToElement(new { status, filePath = path }))]);
            result.SetTimestamp(at);
            messages.Add(result);
        }

        await threads.AppendMessagesAsync(key, [.. messages]);
    }

    private static object Entry(Person p, params string[] oldNames) => new
    {
        name = p.Name,
        telegram = p.Telegram,
        telegramId = p.TelegramId,
        webChat = p.WebChat,
        voice = p.Voice,
        oldNames
    };

    // Fran runs the deployment and owns the household; the Sugary log was Ana's.
    private static Task<(int Exit, string Output)> RunAsync(MigrationRig rig, Person fran, Person ana, params string[] extra) =>
        RunAsync(rig, fran, ana, input: null, extra);

    private static Task<(int Exit, string Output)> RunAsync(
        MigrationRig rig, Person fran, Person ana, string? input, params string[] extra) =>
        rig.RunAsync(
            new[] { Entry(fran), Entry(ana) }, input,
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", ana.Name, .. extra]);
}