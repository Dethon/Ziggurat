using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security.Sealing;
using HouseholdMigration;
using Infrastructure.StateManagers;
using McpServerWebSearch.Handoff;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Migration;

// Two Redis databases of the class's own, as the move sees them: the deployment's, which held the home's
// state while it ran the home's servers, and the tenant kit's, which holds it now.
public sealed class HomeStateRedisFixture : IAsyncLifetime
{
    private RedisLease _deployment = null!;
    private RedisLease _kit = null!;

    public IConnectionMultiplexer Deployment { get; private set; } = null!;

    public IConnectionMultiplexer Kit { get; private set; } = null!;

    public HomeStateSettings Settings => new()
    {
        From = _deployment.ConnectionString,
        To = _kit.ConnectionString
    };

    public async Task InitializeAsync()
    {
        var pool = await RedisPool.GetAsync(RedisPool.KeysPool);
        _deployment = pool.LeaseDatabase();
        _kit = pool.LeaseDatabase();
        Deployment = await _deployment.ConnectAsync();
        Kit = await _kit.ConnectAsync();
    }

    // Each test starts from two empty databases.
    public async Task EmptyAsync()
    {
        await Deployment.GetServer(Deployment.GetEndPoints()[0]).FlushDatabaseAsync(_deployment.Database);
        await Kit.GetServer(Kit.GetEndPoints()[0]).FlushDatabaseAsync(_kit.Database);
    }

    public async Task DisposeAsync()
    {
        await EmptyAsync();
        await Deployment.DisposeAsync();
        await Kit.DisposeAsync();
        _deployment.Return();
        _kit.Return();
    }
}

// The one-off that carries the home's state out of the deployment's Redis into the tenant kit's, as the
// operator runs it when the home moves: written and read through the stores that own it, so a key it
// misses or mangles is a login, a link or a download notice the kit's servers cannot find.
public class HomeStateMoveTests(HomeStateRedisFixture redis) : IClassFixture<HomeStateRedisFixture>, IAsyncLifetime
{
    private static readonly SealingKey _sealingKey = SealingKey.Generate();

    private static readonly PersonState _franRemembers = new(
        """{"cookies":[{"name":"sid","value":"abc","domain":".example.com"}],"origins":[]}""",
        [new SiteLogin("example.com", ["example.com"], DateTimeOffset.UnixEpoch, null)]);

    public Task InitializeAsync() => redis.EmptyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheHomesState_IsCopiedIntoTheKit_AndReadsThereAsItDidInTheDeployment()
    {
        await new RedisPersonStateStore(redis.Deployment, _sealingKey).SaveAsync("user-fran", _franRemembers);
        var link = await new OneTimeLinks(redis.Deployment).MintAsync(new OneTimeLinks.Ticket("handoff", "h-1"), TimeSpan.FromMinutes(10));
        await new RedisDownloadRoutingStore(redis.Deployment).SetAsync(Routing(42));

        var (exit, output) = await RunAsync();

        exit.ShouldBe(0, output);
        (await RememberedAsync(redis.Kit, "user-fran")).ShouldBe(Remembered(_franRemembers));
        (await new RedisDownloadRoutingStore(redis.Kit).ListAsync()).Select(r => r.DownloadId).ShouldBe([42]);
        // Against the deployment's copy, read back to back: Redis expires by the wall clock, and the
        // VM the containers run in steps it backwards, so a reading against the ten minutes it was
        // minted with came back at 10:01.
        var ttl = await redis.Kit.GetDatabase().KeyTimeToLiveAsync($"websearch:link:{link}");
        var deploymentTtl = await redis.Deployment.GetDatabase().KeyTimeToLiveAsync($"websearch:link:{link}");
        ttl.ShouldNotBeNull("a one-time link keeps its expiry");
        (ttl.Value - deploymentTtl!.Value).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
        (await new OneTimeLinks(redis.Kit).ClaimAsync(link, "handoff")).ShouldBe(new OneTimeLinks.Ticket("handoff", "h-1"));
        output.ShouldContain("remembered logins: 1 copied");
        output.ShouldContain("one-time links: 1 copied");
        output.ShouldContain("download routing: 1 copied");
    }

    // A copy: the deployment keeps what it had, which nothing there reads any more.
    [Fact]
    public async Task TheDeploymentsCopy_IsLeftWhereItWas()
    {
        await new RedisDownloadRoutingStore(redis.Deployment).SetAsync(Routing(7));

        await RunAsync();

        (await new RedisDownloadRoutingStore(redis.Deployment).ListAsync()).Select(r => r.DownloadId).ShouldBe([7]);
    }

    // Only the home's own servers' state moves: the deployment's memories, schedules and topics stay.
    [Fact]
    public async Task NothingButTheHomesServersState_IsCopied()
    {
        var deployment = redis.Deployment.GetDatabase();
        await deployment.StringSetAsync("memory:user-fran:m-1", "likes game meat");
        await deployment.StringSetAsync("schedule:s-1", "{}");
        await deployment.StringSetAsync("websearch-elsewhere", "not the browser's");
        await new RedisDownloadRoutingStore(redis.Deployment).SetAsync(Routing(1));

        await RunAsync();

        var kit = redis.Kit.GetServer(redis.Kit.GetEndPoints()[0]);
        var keys = await kit.KeysAsync(redis.Kit.GetDatabase().Database).Select(k => k.ToString()).ToListAsync();
        keys.ShouldBe(["download-routing", "download-routing:1"], ignoreOrder: true);
    }

    [Fact]
    public async Task ADryRun_SaysWhatItWouldCopy_AndWritesNothing()
    {
        await new RedisPersonStateStore(redis.Deployment, _sealingKey).SaveAsync("user-fran", _franRemembers);

        var (exit, output) = await RunAsync("--dry-run");

        exit.ShouldBe(0, output);
        output.ShouldContain("remembered logins: 1 to copy");
        (await new RedisPersonStateStore(redis.Kit, _sealingKey).LoadAsync("user-fran")).ShouldBeNull();
    }

    // Run again, it copies nothing twice and overwrites nothing the kit has since: a login a person
    // made through the kit's browser is newer than the deployment's. The download routing's index is
    // merged, so a download the kit routed since and one the deployment held both stay listed.
    [Fact]
    public async Task ASecondRun_LeavesWhatTheKitAlreadyHas_AndMergesTheDownloadIndex()
    {
        var newer = _franRemembers with { StorageState = """{"cookies":[],"origins":[]}""" };
        await new RedisPersonStateStore(redis.Deployment, _sealingKey).SaveAsync("user-fran", _franRemembers);
        await new RedisDownloadRoutingStore(redis.Deployment).SetAsync(Routing(1));
        await RunAsync();
        await new RedisPersonStateStore(redis.Kit, _sealingKey).SaveAsync("user-fran", newer);
        await new RedisDownloadRoutingStore(redis.Kit).SetAsync(Routing(2));
        await new RedisDownloadRoutingStore(redis.Deployment).SetAsync(Routing(3));

        var (exit, output) = await RunAsync();

        exit.ShouldBe(0, output);
        (await RememberedAsync(redis.Kit, "user-fran")).ShouldBe(Remembered(newer));
        (await new RedisDownloadRoutingStore(redis.Kit).ListAsync()).Select(r => r.DownloadId).ShouldBe([1, 2, 3], ignoreOrder: true);
        output.ShouldContain("remembered logins: 0 copied, 1 already in the kit");
    }

    [Theory]
    [InlineData("", "kit")]
    [InlineData("deployment", "")]
    public async Task WithoutBothRedises_NothingIsCopied_AndItSaysWhich(string from, string to)
    {
        var settings = new HomeStateSettings
        {
            From = from.Length > 0 ? redis.Settings.From : "",
            To = to.Length > 0 ? redis.Settings.To : ""
        };
        await using var output = new StringWriter();

        var exit = await HomeStateMove.RunAsync([], settings, output, CancellationToken.None);

        exit.ShouldBe(2);
        output.ToString().ShouldContain(from.Length == 0 ? "HOMESTATE__FROM" : "HOMESTATE__TO");
    }

    private async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        await using var output = new StringWriter();
        var exit = await HomeStateMove.RunAsync(args, redis.Settings, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    // What a person's record opens to through the store, comparable as text: the storage state and the
    // sites remembered.
    private static async Task<string?> RememberedAsync(IConnectionMultiplexer redis, string person) =>
        await new RedisPersonStateStore(redis, _sealingKey).LoadAsync(person) is { } state ? Remembered(state) : null;

    private static string Remembered(PersonState state) =>
        $"{state.StorageState} {string.Join(",", state.Logins.Select(l => $"{l.Site}:{string.Join("+", l.Domains)}"))}";

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"download {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "user-fran", new ReplyTarget("signalr", $"conv-{id}")),
        SubmittedAt = DateTimeOffset.UnixEpoch
    };
}