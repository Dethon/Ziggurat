using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Memory;
using Infrastructure.StateManagers;
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