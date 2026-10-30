using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.Memory;
using Infrastructure.StateManagers;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Migration;

// The household migration run against seeded stores — the accounts service, Redis, a Sugary log and a
// voices folder — as the operator runs it once on deploy, and again after anything went wrong.
public class HouseholdMigrationCommandTests(HouseholdMigrationRedisFixture redis)
    : IClassFixture<HouseholdMigrationRedisFixture>
{
    private const int Dimension = 4;

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
        await push.SaveAsync(fran.WebChat, phone, $"kitchen-{_tag}");

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franId = (await rig.AccountOfAsync(fran.TelegramId)).UserId;
        (await push.GetBySpaceAsync($"kitchen-{_tag}")).ShouldBe([(franId, phone)]);
        (await push.GetAllAsync()).ShouldNotContain(s => s.UserId == fran.WebChat);
    }

    [Fact]
    public async Task ASchedule_RunsAsTheAccountOfWhoeverWroteIt_OrAsTheOwnerWhereThatWasNobody()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        var schedules = new RedisScheduleStore(redis.Connection);
        var byTelegram = await schedules.CreateAsync(Schedule(new Principal("telegram", fran.Telegram)));
        var byChatClient = await schedules.CreateAsync(Schedule(new Principal("signalr", ana.WebChat.ToUpperInvariant())));
        var byGuest = await schedules.CreateAsync(Schedule(new Principal("voice", "household")));
        var fromBefore = await schedules.CreateAsync(Schedule(null));

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        var franAccount = await rig.AccountOfAsync(fran.TelegramId);
        var anaAccount = await rig.AccountOfAsync(ana.TelegramId);
        (await schedules.GetAsync(byTelegram.Id))!.CreatedBy.ShouldBe(new Principal("telegram", fran.Telegram, franAccount));
        (await schedules.GetAsync(byChatClient.Id))!.CreatedBy.ShouldBe(new Principal("signalr", anaAccount.UserId, anaAccount));
        (await schedules.GetAsync(byGuest.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        (await schedules.GetAsync(fromBefore.Id))!.CreatedBy.ShouldBe(new Principal("signalr", franAccount.UserId, franAccount));
        (await schedules.GetAsync(fromBefore.Id))!.Prompt.ShouldBe(fromBefore.Prompt);
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
        (await threads.GetTopicAsync(anasTopic.AgentId, anasTopic.ChatId, anasTopic.TopicId)).ShouldBe(anasTopic with
        {
            OwnerId = anaAccount.UserId,
            TenantId = anaAccount.TenantId,
            Shared = true,
            OwnerName = ana.Name
        });
        (await threads.GetTopicAsync(guestsTopic.AgentId, guestsTopic.ChatId, guestsTopic.TopicId)).ShouldBe(guestsTopic with
        {
            OwnerId = franAccount.UserId,
            TenantId = franAccount.TenantId,
            Shared = true
        });
        (await threads.GetTopicAsync(someonesPrivate.AgentId, someonesPrivate.ChatId, someonesPrivate.TopicId))
            .ShouldBe(someonesPrivate);
        (await threads.GetTopicAsync(aGroupsCopy.AgentId, aGroupsCopy.ChatId, aGroupsCopy.TopicId)).ShouldBe(aGroupsCopy);
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

    [Fact]
    public async Task RunningItAgain_ChangesNothingAndSaysSo()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedEverythingAsync(rig, fran, ana);
        var first = await RunAsync(rig, fran, ana);
        first.Exit.ShouldBe(0, first.Output);
        var migrated = await SnapshotAsync(rig);

        var (exit, output) = await RunAsync(rig, fran, ana);

        exit.ShouldBe(0, output);
        output.ShouldContain("Nothing changed");
        output.ShouldNotContain("/accounts/enroll/");
        (await SnapshotAsync(rig)).ShouldBe(migrated);
    }

    [Fact]
    public async Task ADryRun_SaysWhatItWouldDoAndWritesNothingAnywhere()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedEverythingAsync(rig, fran, ana);
        var before = await SnapshotAsync(rig);

        var (exit, output) = await RunAsync(rig, fran, ana, "--dry-run");

        exit.ShouldBe(0, output);
        output.ShouldContain("dry run");
        output.ShouldContain($"'{fran.Telegram}'");
        output.ShouldContain($"'{fran.Voice}' renamed");
        output.ShouldContain("would be made");
        (await SnapshotAsync(rig)).ShouldBe(before);
        (await RunAsync(rig, fran, ana)).Exit.ShouldBe(0);
        (await SnapshotAsync(rig)).ShouldNotBe(before);
    }

    [Fact]
    public async Task ARunNamingSomebodyTheFileDoesNotList_MigratesNothing()
    {
        await using var rig = await MigrationRig.StartAsync(redis, Dimension);
        var (fran, ana) = (Fran(), Ana());
        await SeedEverythingAsync(rig, fran, ana);
        var before = await SnapshotAsync(rig);

        var (exit, output) = await rig.RunAsync(
            new[] { Entry(fran), Entry(ana) },
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", $"Bea-{_tag}"]);

        exit.ShouldBe(2);
        output.ShouldContain($"Bea-{_tag} is not one of the people listed");
        (await SnapshotAsync(rig)).ShouldBe(before);
    }

    private async Task SeedEverythingAsync(MigrationRig rig, Person fran, Person ana)
    {
        await Memories().StoreAsync(Memory(fran.Telegram, $"Likes game meat {_tag}", [1, 0, 0, 0]));
        await Memories().StoreAsync(Memory("household", $"The cat is called Mus {_tag}", [1, 1, 0, 0]));
        await new RedisPersonStateStore(redis.Connection, MigrationRig.StateKey)
            .SaveAsync(fran.Name, new PersonState("{}", []));
        await new RedisPushSubscriptionStore(redis.Connection)
            .SaveAsync(ana.WebChat, new PushSubscriptionDto($"https://push.example/{_tag}", "p", "a"));
        await new RedisScheduleStore(redis.Connection).CreateAsync(Schedule(new Principal("telegram", fran.Telegram)));
        await TopicAsync(
            new RedisThreadStateStore(redis.Connection, new RetentionSettings(), TimeProvider.System), ana.WebChat);
        Directory.CreateDirectory(Path.Combine(rig.Voices, fran.Voice));
        await File.WriteAllBytesAsync(Path.Combine(rig.Voices, fran.Voice, "take-1.wav"), [1]);
        await using var db = await rig.Sugary.CreateDbContextAsync();
        db.Meals.Add(new Meal
        {
            UserId = Owners.Unassigned, TenantId = Owners.Unassigned, Id = Guid.NewGuid(), EatenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

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
        RedisThreadStateStore threads, string firstSender, Func<TopicMetadata, TopicMetadata>? owned = null)
    {
        var now = DateTimeOffset.UtcNow;
        var topic = new TopicMetadata(
            Guid.NewGuid().ToString("N"), Random.Shared.NextInt64(1, long.MaxValue), Random.Shared.NextInt64(1, 1_000_000),
            "jonas", $"Topic {_tag}", now, now, "default");
        topic = owned?.Invoke(topic) ?? topic;
        await threads.SaveTopicAsync(topic);
        var first = new ChatMessage(ChatRole.User, "What's for dinner?");
        first.SetSenderId(firstSender);
        await threads.AppendMessagesAsync(
            new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString(),
            [first, new ChatMessage(ChatRole.Assistant, "Pasta.")]);
        return (await threads.GetTopicAsync(topic.AgentId, topic.ChatId, topic.TopicId))!;
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
        rig.RunAsync(
            new[] { Entry(fran), Entry(ana) },
            ["--operator", fran.Name, "--owner", fran.Name, "--sugary", ana.Name, .. extra]);
}