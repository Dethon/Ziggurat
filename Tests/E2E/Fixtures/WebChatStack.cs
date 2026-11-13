using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accounts;
using Accounts.Configuration;
using Accounts.People;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Agent.App;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Security;
using Domain.Security.Sealing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tests.Integration.Fixtures;

namespace Tests.E2E.Fixtures;

// The containers behind every WebChat E2E collection, owned process-wide rather than per fixture.
//
// The suite used to run every WebChat E2E class as one serial chain behind a single collection,
// which was the whole of its wall clock: ~150s during which nothing else in the run was left to do.
// Splitting those classes into collections that run at once is only worth it if they keep sharing
// one stack — six containers per collection would cost more in start-up than the serialisation
// ever cost in waiting. So the two expensive phases are memoized (one build, one start, whoever
// asks first) and the containers are refcounted, torn down when the last collection lets go.
//
// Concurrency across collections is safe because a test's server-side state is scoped to the account
// it signs in as, and each collection reserves its own block of user identities from this stack.
internal sealed partial class WebChatStack
{
    // Where the stack's agent publishes its keys, which every server checks a tool call's token
    // against, as compose wires it.
    private const string AgentKeysUrl = $"http://agent:8080{AgentKeysApi.Path}";

    // The agent's credential with the accounts service (CALLERS__agent), its own API's secret as
    // compose hands it.
    private const string AgentCredential = "e2e-agent-api-secret";

    private static readonly Lock _gate = new();
    private static Task? _images;
    private static Task<WebChatStack>? _started;
    private static int _refCount;

    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpVault;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;
    private IContainer? _mcpSugary;
    private IContainer? _accounts;
    private WebApplication? _whisper;
    private FakeKeyProviders? _keyProviders;
    private int _caddyPort;

    private int _sliceIndex = -1;

    // A space is what separates one collection's conversations from another's. The sidebar asks
    // GetAllTopics(agentId, spaceSlug) — it is scoped by agent and space and not by user — so a
    // topic the chat suite renamed was appearing at the top of the gesture suite's sidebar and
    // being tapped instead of the row that suite had just made. Per-collection users do not fix
    // that and did not; a space is the boundary the application itself draws, so each collection
    // takes one. They must be spaces of the household's configuration, which the stack saves as it
    // starts, or the client opens the household's first space instead.
    private static readonly string[] _spaces =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor"];

    // A space no collection works in, with a name, colour and agent of its own: what a person sees of
    // a space is its configuration's.
    public static readonly HouseholdSpace Kitchen = new()
    {
        Slug = "kilo", Name = "Kilo Kitchen", AccentColor = "#2a9d8f", DefaultAgent = "vision-agent", Order = 99
    };

    // A user identity is an account in the stack's accounts service, made the first time a test
    // asks for its index and signed in by a minted session rather than a passkey ceremony, so no
    // test needs to inherit one, and a block is sized past the number of tests in its collection so
    // none is handed out twice. A
    // pending approval is held against its topic, and a page opening as a user who already has
    // topics can be handed one, which is how a test that never asked for an approval met the
    // full-viewport modal and clicked a button that detached from under it. Raise these when a
    // collection grows past its block rather than letting the counter wrap.
    public const int UsersPerCollection = 24;
    public const int CollectionSlices = 21;

    public string WebChatUrl { get; private set; } = "";

    // The Sugary app and its API, published as compose publishes them (port 6017 there): straight
    // from the Sugary server, not through the Caddy site a phone uses, so a test sends the session
    // check's headers for the account it acts as (AccountForAsync) as that site's proxy would.
    public string SugaryUrl { get; private set; } = "";

    // The Sugary server's /mcp, where the tables an agent reads under /sugary are served.
    public string SugaryMcpEndpoint { get; private set; } = "";

    // How long the Sugary server waits on the agent's estimate before answering 504. An estimate is
    // a real model's answer over the network, so it is bounded by the provider, as the chat E2E's
    // reply is (three minutes), and not by the shipped two minutes meant for her phone.
    public static TimeSpan SugaryTurnTimeout { get; } = TimeSpan.FromMinutes(3);

    // What the stubbed whisper answers, per test. No model and no GPU are involved: the browser,
    // the channel server, the ticket and the upload are all real, and only the transcription is
    // decided here.
    //
    // Kept per space, because that is what lets more than one dictation collection run at once. One
    // shared answer meant one collection: thirteen tests each setting their own words and asserting
    // those exact words arrive, so two in flight would overwrite and read each other's. The
    // recording now arrives named for the space it was spoken in, and each collection has a space
    // of its own, so the stub can answer them separately.
    private readonly Dictionary<string, string> _transcripts = [];
    private readonly Dictionary<string, int> _statuses = [];
    private readonly Dictionary<string, byte[]> _lastAudio = [];
    private readonly Lock _stub = new();

    private const string DefaultTranscript = "hola desde el micrófono";

    public string TranscriptFor(string space)
    {
        lock (_stub)
        {
            return _transcripts.TryGetValue(space, out var t) ? t : DefaultTranscript;
        }
    }

    public void SetTranscript(string space, string transcript)
    {
        lock (_stub)
        {
            _transcripts[space] = transcript;
        }
    }

    public int StatusFor(string space)
    {
        lock (_stub)
        {
            return _statuses.TryGetValue(space, out var s) ? s : 200;
        }
    }

    public void SetTranscriptionStatus(string space, int status)
    {
        lock (_stub)
        {
            _statuses[space] = status;
        }
    }

    public byte[]? LastAudioFor(string space)
    {
        lock (_stub)
        {
            return _lastAudio.TryGetValue(space, out var a) ? a : null;
        }
    }

    // Long enough that no gesture a case makes can run into it. It used to be four seconds, so the
    // one case about the cap could hold the microphone past it without a two-minute wait — but a
    // cap is stack-wide, and every other dictation case inherited it. A hold plus eight CDP moves
    // is about a second on a quiet machine and past four seconds on a loaded one, at which point
    // the cap ended the recording mid-swipe and transcribed it: the discard case found the words
    // it had just thrown away sitting in the composer, and reported it as the discard leaking.
    //
    // The case that is about the cap shortens it for its own page instead, which it can because
    // the browser learns the number from .NET and carries none of its own.
    public TimeSpan RecordingCap { get; } = TimeSpan.FromMinutes(2);

    // A collection's own slice of the stack: the first index of its block of users, and the space
    // its pages live in. Reserved once per collection, in whatever order they start.
    public (int UserBlock, string Space) ReserveSlice()
    {
        var slice = (int)((uint)Interlocked.Increment(ref _sliceIndex) % CollectionSlices);
        return (slice * UsersPerCollection, _spaces[slice]);
    }

    public static string NameOf(int userIndex) => $"TestUser-{userIndex}";

    // The session cookie's value for a user index, the account behind it made on first asking. Each
    // command is a .NET process started inside the container, about a second, so making all of the
    // stack's users up front would cost minutes nobody uses.
    public async Task<string> SessionForAsync(int userIndex) => (await SeededAsync(userIndex)).Session;

    // The stack's people, made before the accounts service starts rather than one command at a time
    // while the tests wait: the store is built in this process with the service's own stores (the
    // same migrations, the same home household, a session issued as mint-session issues one) and
    // copied in. Each collection's first users by index, and a pool of owners and members a test
    // that needs a person of its own takes from (PersonAsync). Every command made two .NET processes
    // start in the container per person, which was the E2E run's floor and a fifth of its CPU.
    private const string SeededStorePath = "/tmp/accounts.db";

    // The .NET images' own non-root user, which the service runs as ($APP_UID), so the copied store
    // is its own to write.
    private const uint AppUid = 1654;
    private const int SeededPerCollection = 12;
    private const int PooledPerRole = 32;
    private const int PooledElsewhere = 16;

    // The key households' secrets are sealed under, made for this stack: the second household's own
    // OpenRouter key is sealed with it in the seeded store, and the service opens it with the same.
    private static readonly string MasterKey = Convert.ToBase64String(SealingKey.Generate().Export());
    private readonly ConcurrentDictionary<(bool Elsewhere, Role Role), ConcurrentQueue<Seeded>> _pool = new();
    private Seeded? _probe;

    private async Task<byte[]> SeedAccountsStoreAsync(CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "e2e-accounts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "accounts.db");
        try
        {
            var settings = new AccountsSettings
            {
                DatabasePath = path,
                HouseholdConfig = new HouseholdConfigSettings { MasterKey = MasterKey }
            };
            var clock = new AccountsClock(TimeProvider.System);
            await using (var services = new ServiceCollection().AddAccountsCore(settings, clock).BuildServiceProvider())
            {
                await AccountsDatabase.PrepareAsync(
                    services.GetRequiredService<IDbContextFactory<AccountsDbContext>>(), clock.Time, ct);
                var people = services.GetRequiredService<PersonStore>();
                var sessions = services.GetRequiredService<SessionStore>();

                async Task<Seeded> MakeAsync(string name, Role role)
                {
                    var person = await people.CreateAsync(name, role, ct);
                    var session = await sessions.IssueAsync(person.Id, credentialId: null, device: "mint-session", ct)
                        ?? throw new InvalidOperationException($"no session for {person.Id}");
                    return new Seeded(session.Token, new Account(person.TenantId, person.Id));
                }

                foreach (var index in Enumerable.Range(0, CollectionSlices)
                             .SelectMany(slice => Enumerable.Range(slice * UsersPerCollection, SeededPerCollection)))
                {
                    var seeded = await MakeAsync(NameOf(index), Role.Member);
                    _sessions[index] = new Lazy<Task<Seeded>>(Task.FromResult(seeded));
                }

                // The person the stack signs in as to know it is up, end to end.
                _probe = await MakeAsync("Probe", Role.Member);

                // The home's agents and spaces, saved as set-household-config saves them. Nobody is
                // listening yet to be told, and every reader starts after this.
                var config = services.GetRequiredService<HouseholdConfigStore>();
                var agents = await config.SaveAsync(
                    _probe.Account.TenantId, HouseholdSections.AgentsSchema, HouseholdSections.AgentsSchema.Write(Agents), ct);
                var spaces = await config.SaveAsync(
                    _probe.Account.TenantId, HouseholdSections.SpacesSchema, HouseholdSections.SpacesSchema.Write(Spaces), ct);
                if (agents is not SectionSave.Saved || spaces is not SectionSave.Saved)
                {
                    throw new InvalidOperationException($"the seeded store refused a section: {agents}, {spaces}");
                }

                foreach (var role in new[] { Role.Owner, Role.Member })
                {
                    var queue = _pool.GetOrAdd((false, role), _ => new ConcurrentQueue<Seeded>());
                    foreach (var n in Enumerable.Range(0, PooledPerRole))
                    {
                        queue.Enqueue(await MakeAsync($"Pooled {role} {n}", role));
                    }
                }

                // A second household, for the tests that change who its people are: a role, sees-all
                // or a member disabled is announced to the household, and the hub has every open page
                // of it renew its connection — a dictation or a gesture caught mid-renewal in the home,
                // where every other collection works, fails for a reason that is no fault of its own.
                var contexts = services.GetRequiredService<IDbContextFactory<AccountsDbContext>>();
                var elsewhere = Tokens.NewId();
                await using (var db = await contexts.CreateDbContextAsync(ct))
                {
                    db.Tenants.Add(new Tenant { Id = elsewhere, Name = "Elsewhere", CreatedAt = clock.Time.GetUtcNow() });
                    await db.SaveChangesAsync(ct);
                }

                async Task<Seeded> MakeElsewhereAsync(string name, Role role)
                {
                    var id = Tokens.NewId();
                    await using (var db = await contexts.CreateDbContextAsync(ct))
                    {
                        db.Users.Add(new User
                        {
                            Id = id, TenantId = elsewhere, DisplayName = name, Role = role, CreatedAt = clock.Time.GetUtcNow()
                        });
                        await db.SaveChangesAsync(ct);
                    }

                    var session = await sessions.IssueAsync(id, credentialId: null, device: "mint-session", ct)
                        ?? throw new InvalidOperationException($"no session for {id}");
                    return new Seeded(session.Token, new Account(elsewhere, id));
                }

                // Its agents are the home's, and it pays for its turns with a key of its own (the same
                // one), since a household that is not the home has no deployment key to fall back on.
                var elsewhereAgents = await config.SaveAsync(
                    elsewhere, HouseholdSections.AgentsSchema, HouseholdSections.AgentsSchema.Write(Agents), ct);
                var elsewhereKeys = await config.SaveAsync(
                    elsewhere, HouseholdSections.KeysSchema,
                    HouseholdSections.KeysSchema.Write(new KeysSection { OpenRouter = new OpenRouterKey(GetOpenRouterApiKey()!) }), ct);
                if (elsewhereAgents is not SectionSave.Saved || elsewhereKeys is not SectionSave.Saved)
                {
                    throw new InvalidOperationException($"the seeded store refused a section: {elsewhereAgents}, {elsewhereKeys}");
                }

                foreach (var role in new[] { Role.Owner, Role.Member })
                {
                    var queue = _pool.GetOrAdd((true, role), _ => new ConcurrentQueue<Seeded>());
                    foreach (var n in Enumerable.Range(0, PooledElsewhere))
                    {
                        queue.Enqueue(await MakeElsewhereAsync($"Pooled {role} {n}", role));
                    }
                }
            }

            SqliteConnection.ClearAllPools();
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    // A person of the home household a test makes for itself, by the name and role it gives: one of
    // the pool, renamed through their own account page as they could rename themselves, or — once the
    // pool is spent — made by the service's commands as an operator would.
    public async Task<(string UserId, string Session)> PersonAsync(string name, string role, bool elsewhere = false)
    {
        var wanted = role == "owner" ? Role.Owner : Role.Member;
        if (_pool.TryGetValue((elsewhere, wanted), out var queue) && queue.TryDequeue(out var pooled))
        {
            await RenameAsync(pooled.Session, name);
            return (pooled.Account.UserId, pooled.Session);
        }

        if (elsewhere)
        {
            throw new InvalidOperationException(
                "the second household's pool is spent: raise PooledElsewhere, since the commands make people of the home alone");
        }

        var (_, created) = await AccountsAsync("create-user", "--name", name, "--role", role);
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        var (_, session) = await AccountsAsync("mint-session", "--user", user);
        return (user, session.Trim());
    }

    private async Task RenameAsync(string session, string name)
    {
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(WebChatUrl)
        };
        var cookies = $"{SessionCookieName}={session}";
        using var read = new HttpRequestMessage(HttpMethod.Get, "accounts/account");
        read.Headers.Add("Cookie", cookies);
        using var page = await client.SendAsync(read);
        page.EnsureSuccessStatusCode();
        var antiforgery = page.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(c => c.Split(';')[0]))
            : "";
        var field = AntiforgeryField().Match(await page.Content.ReadAsStringAsync());
        if (!field.Success)
        {
            throw new InvalidOperationException("the account page carried no antiforgery field");
        }

        using var save = new HttpRequestMessage(HttpMethod.Post, "accounts/account/profile")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["displayName"] = name,
                ["avatarUrl"] = "",
                [field.Groups["name"].Value] = WebUtility.HtmlDecode(field.Groups["value"].Value)
            })
        };
        save.Headers.Add("Cookie", antiforgery.Length > 0 ? $"{cookies}; {antiforgery}" : cookies);
        using var saved = await client.SendAsync(save);
        if (saved.StatusCode != HttpStatusCode.SeeOther)
        {
            throw new InvalidOperationException(
                $"renaming a pooled person answered {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
        }
    }

    private const string SessionCookieName = "ziggurat_session";

    [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]+)" value="(?<value>[^"]*)">""")]
    private static partial Regex AntiforgeryField();

    // Starts making a user a collection is about to ask for, so its test finds it made rather than
    // waiting the two commands out. A failure is kept in the memoized task and reaches whichever
    // test asks for that index; nobody asking means nobody needed it.
    public void Prefetch(int userIndex) =>
        _ = SeededAsync(userIndex).ContinueWith(
            t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // The account behind a user index, as the session check names it in its headers: what a test
    // that reaches an app past the proxy presents to act as that person.
    public async Task<Account> AccountForAsync(int userIndex) => (await SeededAsync(userIndex)).Account;

    private Task<Seeded> SeededAsync(int userIndex) =>
        _sessions.GetOrAdd(userIndex, index => new Lazy<Task<Seeded>>(() => SeedAsync(NameOf(index)))).Value;

    private sealed record Seeded(string Session, Account Account);

    private readonly ConcurrentDictionary<int, Lazy<Task<Seeded>>> _sessions = new();
    // One command at a time, as they write one SQLite file. Few are left: the stack's people are
    // seeded before the service starts, so these are the commands a test is about (a link, a second
    // session) and the pool's overflow.
    private readonly SemaphoreSlim _seeding = new(1, 1);

    private async Task<Seeded> SeedAsync(string name)
    {
        var (_, created) = await AccountsAsync("create-user", "--name", name);
        var made = WhoWasMade().Match(created);
        var account = new Account(made.Groups["household"].Value, made.Groups["user"].Value);
        var (_, session) = await AccountsAsync("mint-session", "--user", account.UserId);
        return new Seeded(session.Trim(), account);
    }

    // The accounts service's own commands, as the operator runs them: docker exec accounts dotnet
    // Accounts.dll <verb>. A command that fails fails the test that asked, with what it printed.
    public async Task<(int Exit, string Output)> AccountsAsync(params string[] args)
    {
        var queued = Stopwatch.StartNew();
        await _seeding.WaitAsync();
        E2ETrace.Write("accounts:queued", queued.Elapsed.TotalSeconds, args[0]);
        try
        {
            var result = await E2ETrace.TimeAsync($"accounts:{args[0]}", () => _accounts!.ExecAsync(["dotnet", "Accounts.dll", .. args]));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"accounts {string.Join(' ', args)} exited {result.ExitCode}: {result.Stdout}{result.Stderr}");
            }

            return ((int)result.ExitCode, result.Stdout);
        }
        finally
        {
            _seeding.Release();
        }
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household (?<household>[0-9a-f]+)")]
    private static partial Regex WhoWasMade();

    // Both phases are memoized separately so each keeps the caller's own budget: an image build is
    // allowed twenty minutes and a container start five, and folding them together would have a
    // cold build spend the smaller one. Whoever asks first does the work; the rest await it.
    public static Task EnsureImagesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _images ??= BuildImagesAsync(ct);
        }
    }

    public static async Task<WebChatStack> AcquireAsync(CancellationToken ct)
    {
        Task<WebChatStack> started;
        lock (_gate)
        {
            started = _started ??= StartAsync(ct);
            _refCount++;
        }

        return await started;
    }

    // The last collection to finish takes the containers down. Anything else would leave them for
    // the run's lifetime, or tear them out from under a collection still using them.
    public static async Task ReleaseAsync()
    {
        WebChatStack? stack = null;
        Task<WebChatStack>? started;
        lock (_gate)
        {
            if (--_refCount > 0)
            {
                return;
            }

            started = _started;
            _started = null;
            _images = null;
        }

        if (started is not null)
        {
            try
            {
                stack = await started;
            }
            catch
            {
                // A stack that never started has nothing to dispose; the failure already reached
                // whoever awaited the acquire.
            }
        }

        if (stack is not null)
        {
            await stack.StopAsync();
        }
    }

    private static async Task BuildImagesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(GetOpenRouterApiKey()))
        {
            return;
        }

        var solutionRoot = TestHelpers.FindSolutionRoot();

        // Every leaf image is FROM base-sdk:latest, so this must complete first.
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);

        // Leaf images are mutually independent; EnsureImageAsync serialises per-tag
        // internally, so distinct tags building at once is safe.
        await Task.WhenAll(
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.McpVault, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.ChannelSignalR, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Agent, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.WebUi, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.McpSugary, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Accounts, ct));
    }

    private static async Task<WebChatStack> StartAsync(CancellationToken ct)
    {
        var stack = new WebChatStack();
        await stack.StartContainersAsync(ct);
        return stack;
    }

    private async Task StartContainersAsync(CancellationToken ct)
    {
        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        var stackElapsed = Stopwatch.StartNew();
        _network = TestContainers.Network()
            .WithName($"e2e-webchat-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        var mcpVaultImageName = E2EImages.McpVault.ImageName;
        var signalRImageName = E2EImages.ChannelSignalR.ImageName;
        var agentImageName = E2EImages.Agent.ImageName;
        var webuiImageName = E2EImages.WebUi.ImageName;

        // The stack image, not plain redis: topic search indexes through RediSearch, and on a
        // redis without it the search path degrades to empty results — a stack on plain redis
        // would pass E2E while exercising the fallback instead of the feature. The slower boot
        // this image costs was once traded away (27.7s to 14.0s) when nothing here indexed
        // anything; that stopped being true when the chat channel started writing search
        // documents.
        _redis = TestContainers.Container("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();

        _mcpVault = TestContainers.Container(mcpVaultImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-vault")
            .WithEnvironment("VAULTPATH", "/vault")
            // The credential this server's /mcp asks for and the agent presents to it alone,
            // exactly as compose wires it.
            .WithEnvironment("MCP__SHAREDSECRET", McpTestSecret.For("mcp-vault"))
            .WithEnvironment("MCP__AUDIENCE", "mcp-vault")
            .WithEnvironment("MCP__AGENTKEYSURL", AgentKeysUrl)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
            .Build();

        // The Sugary log's server, wired as compose wires it: its own credential on /mcp and the
        // home's zone (a meal is filed under the month it was eaten in Madrid). Compose mounts
        // ./volumes/sugary at /data; a tmpfs stands in, world-writable as Docker makes one, so the log
        // and the uploads keep their shipped paths and start empty.
        _mcpSugary = TestContainers.Container(E2EImages.McpSugary.ImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-sugary")
            .WithTmpfsMount("/data")
            .WithEnvironment("TZ", "Europe/Madrid")
            .WithEnvironment("MCP__SHAREDSECRET", McpTestSecret.For("mcp-sugary"))
            .WithEnvironment("MCP__AUDIENCE", "mcp-sugary")
            .WithEnvironment("MCP__AGENTKEYSURL", AgentKeysUrl)
            .WithEnvironment("TURNTIMEOUTSECONDS", ((int)SugaryTurnTimeout.TotalSeconds).ToString())
            .WithPortBinding(8080, true)
            // The app's session route answers once the database is migrated and the routes are bound:
            // 401 to a request naming nobody, which is every request here but the tests' own.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/api/session")
                    .ForStatusCode(HttpStatusCode.Unauthorized)))
            .Build();

        // The browser reaches everything through Caddy at localhost on a port taken here rather than
        // one Docker picks, because the accounts service has to be told where it is served before
        // Caddy exists: a passkey belongs to its relying party, which cannot be an IP address, and
        // sign-in returns only to the origins it was given. The session cookie is host-only, so the
        // pages and sign-in share this one host exactly.
        _caddyPort = TestPort.GetAvailable();
        var baseUrl = $"http://localhost:{_caddyPort}";

        // The accounts service, as compose wires it but for this host: its store in a tmpfs that
        // starts empty, migrated before the sign-in page answers.
        // The providers a household's keys are checked with, answered in this process and reached
        // through the host's gateway, so an owner's key is never put to a real provider. Started
        // first: the accounts service is told where they are.
        _keyProviders = await FakeKeyProviders.StartAsync(forContainers: true);

        var seededStore = await E2ETrace.TimeAsync("seed:accounts", () => SeedAccountsStoreAsync(ct));
        _accounts = TestContainers.Container(E2EImages.Accounts.ImageName)
            // The store the stack's people were made in by this process (SeedAccountsStoreAsync), copied
            // in before the service starts and kept where its user can write it and the journal beside
            // it: /data is a tmpfs, mounted at start over anything copied there at create. Not copied by
            // a shell entrypoint, which drops the CALLERS__<host> variables a hyphen makes no shell
            // name of. The commands run in the container read the same variable.
            .WithResourceMapping(seededStore, SeededStorePath, AppUid, AppUid,
                UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithEnvironment("DATABASEPATH", SeededStorePath)
            .WithEnvironment("HOUSEHOLDCONFIG__MASTERKEY", MasterKey)
            .WithNetwork(_network)
            .WithNetworkAliases("accounts")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("KEYPROBES__OPENROUTERURL", _keyProviders.OpenRouterUrlOn("host.docker.internal"))
            .WithEnvironment("KEYPROBES__IDEALISTAURL", _keyProviders.RootOn("host.docker.internal"))
            .WithEnvironment("KEYPROBES__TELEGRAMURL", _keyProviders.RootOn("host.docker.internal"))
            .WithTmpfsMount("/data")
            .WithEnvironment("WEBAUTHN__RELYINGPARTYID", "localhost")
            .WithEnvironment("ALLOWEDORIGINS__0", baseUrl)
            .WithEnvironment("PUBLICBASEURL", baseUrl)
            // The stack's fixed key (Tests/TestSigningKeys), as every test stack runs its issuers
            // with, so a run signs the same way every time.
            .WithEnvironment("SIGNING__PRIVATEKEY", TestSigningKeys.Accounts)
            // The hub asks the name of each connection's account with its own MCP credential, as
            // compose wires it.
            .WithEnvironment("CALLERS__mcp-channel-signalr", McpTestSecret.For("mcp-channel-signalr"))
            // Where a saved household configuration is announced, as compose wires it: the stack saves
            // its spaces, and a save nobody could be told of is a failed command.
            .WithEnvironment("REDIS__CONNECTIONSTRING", "redis:6379")
            // Sugary reads its people's household zone with its own credential, proving the household
            // with a call's token (checked against the agent's keys) or a request's assertion; a
            // household's default zone is this service's, the home's as compose's shared TZ makes it.
            .WithEnvironment("CALLERS__mcp-sugary", McpTestSecret.For("mcp-sugary"))
            .WithEnvironment("AGENTKEYSURL", AgentKeysUrl)
            // The agent reads its households' configuration with the credential it holds for its own
            // API, as compose wires it, beside a token it signs for the configuration.
            .WithEnvironment("CALLERS__agent", AgentCredential)
            .WithEnvironment("TZ", "Europe/Madrid")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/accounts/sign-in")))
            .Build();

        // Started together because nothing here waits on anything else here: redis, the vault,
        // the Sugary server and the accounts service are roots of the graph and the whisper stub
        // runs in this process. Starting them in a line spent each one's wait doing nothing, and
        // redis alone is the longest of them.
        var whisperPortTask = StartWhisperStubAsync();
        await Task.WhenAll(
            E2ETrace.TimeAsync("start:redis", () => _redis.StartAsync(ct)),
            E2ETrace.TimeAsync("start:vault", () => _mcpVault.StartAsync(ct)),
            E2ETrace.TimeAsync("start:sugary", () => _mcpSugary.StartAsync(ct)),
            E2ETrace.TimeAsync("start:accounts", () => _accounts.StartAsync(ct)),
            whisperPortTask);
        var whisperPort = await whisperPortTask;

        _mcpChannelSignalR = TestContainers.Container(signalRImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("MCP__SHAREDSECRET", McpTestSecret.For("mcp-channel-signalr"))
            .WithEnvironment("MCP__AUDIENCE", "mcp-channel-signalr")
            .WithEnvironment("MCP__AGENTKEYSURL", AgentKeysUrl)
            // Dictation reaches whisper through the shared transcription client, so pointing its
            // base URL at the test's own listener is the whole of the substitution.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment(
                "DICTATION__TRANSCRIPTION__BASEURL", $"http://host.docker.internal:{whisperPort}/v1")
            .WithEnvironment("DICTATION__MAXLENGTH", RecordingCap.ToString())
            // One row a page, so a suite that can afford two conversations still exercises the
            // second fetch. The production default is thirty and lives in appsettings.
            .WithEnvironment("RETENTION__PAGESIZE", "1")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .WithEnvironment("AGENTS__1__ID", "vision-agent")
            .WithEnvironment("AGENTS__1__NAME", "Vision Agent")
            // POST negotiate succeeds only after Kestrel has fully bound routes; TCP-only
            // checks return before the app is actually serving requests.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(8080)
                    .ForPath("/hubs/chat/negotiate")
                    .WithMethod(HttpMethod.Post)))
            .Build();
        await E2ETrace.TimeAsync("start:signalr", () => _mcpChannelSignalR.StartAsync(ct));

        // Inject a minimal appsettings.json so the agent only connects to E2E services.
        // Raise with E2E_AGENT_LOG_LEVEL=Debug when a failing run needs the agent's own account
        // of a turn rather than the browser's.
        var agentLogLevel = Environment.GetEnvironmentVariable("E2E_AGENT_LOG_LEVEL") ?? "Information";
        // Without this, the default appsettings.json baked into the image also registers
        // channels for telegram and extra MCP endpoints that don't exist here.
        var e2EAppSettings = Encoding.UTF8.GetBytes($$"""
            {
              "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "{{apiKey}}" },
              "redis": { "connectionString": "redis:6379" },
              "backCalls": { "lemonadeUrl": "http://lemonade:13305" },
              // Without these the model/effort menu never renders, so nothing in the suite ever
              // exercised the control the drawer's gesture bugs live next to.
              "patchableModels": [
                { "id": "~deepseek/deepseek-v4-flash-latest:nitro", "name": "DeepSeek V4 Flash" },
                { "id": "google/gemini-3.1-flash-lite", "name": "Gemini 3.1 Flash Lite" }
              ],
              // The app's messages name no agent; the deployment routes the channel to sugary.
              "agentDefaults": { "byChannel": { "sugary": "sugary" } },
              "channelEndpoints": [
                // WebChat hosts the review copy of every estimate, as it does in the deployment.
                // Under the id the deployment gives it: the hub's assertions name `signalr`, and the agent
                // refuses a turn whose assertion names another channel than the one it came on.
                { "channelId": "signalr", "endpoint": "http://mcp-channel-signalr:8080/mcp", "hostsReviewCopies": true },
                { "channelId": "sugary", "endpoint": "http://mcp-sugary:8080/mcp" }
              ],
              "Logging": { "LogLevel": { "Default": "{{agentLogLevel}}" } }
            }
            """);

        _agent = TestContainers.Container(agentImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("agent")
            .WithCommand("--chat", "Web", "--reasoning")
            .WithResourceMapping(e2EAppSettings, "/app/appsettings.json")
            // Each server its own credential, as compose wires it: the agent holds them by host.
            .WithEnvironment("MCP__SECRETS__mcp-vault", McpTestSecret.For("mcp-vault"))
            .WithEnvironment("MCP__SECRETS__mcp-sugary", McpTestSecret.For("mcp-sugary"))
            .WithEnvironment("MCP__SECRETS__mcp-channel-signalr", McpTestSecret.For("mcp-channel-signalr"))
            // The home's agents are its configuration's, read from the accounts service with this.
            .WithEnvironment("AGENTAPI__SHAREDSECRET", AgentCredential)
            // The suite's fixed key instead of one generated into a volume, so the tests' own
            // clients, which sign with the same key, are the agent as far as the servers can tell.
            .WithEnvironment("SIGNING__PRIVATEKEY", TestSigningKeys.Agent)
            // Wait until Kestrel + hosted services (ChatMonitoring) are running. The agent
            // container has no published host port, so an HTTP probe isn't possible; the
            // log line is the cheapest reliable signal.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Application started"))
            .Build();

        _webui = TestContainers.Container(webuiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("webui")
            .WithPortBinding(8080, true)
            // Empty string: browser falls back to relative /hubs/chat, which Caddy routes to mcp-channel-signalr
            .WithEnvironment("AGENTURL", "")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/manifest.webmanifest")))
            .Build();

        // The webui serves files and reaches nothing, so it never had to follow the agent — and the
        // agent's wait is for a log line the webui cannot affect. Caddy is what needs them both,
        // and it is next. Starting it beside them saves its own boot and was tried: it also puts
        // three container starts into the same window as every other fixture's, and the host answers
        // that by timing one of them out somewhere else in the suite — two runs in four lost the
        // Jackett and library-server fixtures to it. Three seconds is not worth buying with that.
        await Task.WhenAll(
            E2ETrace.TimeAsync("start:agent", () => _agent.StartAsync(ct)),
            E2ETrace.TimeAsync("start:webui", () => _webui.StartAsync(ct)));

        // DockerCompose/caddy/Caddyfile's main host, minus TLS: the chat client, its hub and the
        // hub's upload and dictation endpoints ask the accounts service's session check first and
        // carry the person on in its headers; sign-in and the agent API do not.
        static string Checked(string matcher, string upstream, string channel = ChannelIds.SignalR) =>
            $"    handle {matcher}{{\n" +
            "        forward_auth accounts:8080 {\n" +
            $"            uri /accounts/verify?channel={channel}\n" +
            "            copy_headers X-Ziggurat-User X-Ziggurat-Tenant X-Ziggurat-Roles X-Ziggurat-Assertion\n" +
            "        }\n" +
            $"        reverse_proxy {upstream}\n" +
            "    }\n";

        var testCaddyfile =
            ":80 {\n" +
            Checked("/hubs/* ", "mcp-channel-signalr:8080") +
            // The upload store lives beside the hub; without this route an upload POST lands on
            // the static webui and comes back 405, mirroring DockerCompose/caddy/Caddyfile.
            Checked("/api/attachments* ", "mcp-channel-signalr:8080") +
            // Without this route a dictation POST lands on the static webui and comes back 405,
            // mirroring DockerCompose/caddy/Caddyfile.
            Checked("/api/dictation* ", "mcp-channel-signalr:8080") +
            "    handle /api/agents* {\n" +
            "        reverse_proxy agent:8080\n" +
            "    }\n" +
            "    handle /accounts/internal/* {\n" +
            "        respond 404\n" +
            "    }\n" +
            // The session check is forward_auth's alone, mirroring DockerCompose/caddy/Caddyfile.
            "    handle /accounts/verify* {\n" +
            "        respond 404\n" +
            "    }\n" +
            // The admin area's API, behind the session check for the admin channel and before the
            // accounts service's unchecked pages, mirroring DockerCompose/caddy/Caddyfile.
            Checked("/accounts/admin/* ", "accounts:8080", AppChannels.Admin) +
            "    handle /accounts/* {\n" +
            "        reverse_proxy accounts:8080\n" +
            "    }\n" +
            // The app's own files to a signed-out browser, which is how an installed app from before
            // sign-in replaces its worker, mirroring DockerCompose/caddy/Caddyfile's @shell.
            "    @shell {\n" +
            "        not header Sec-Fetch-Mode navigate\n" +
            "        not header Accept *text/html*\n" +
            "        not path /api/*\n" +
            "    }\n" +
            "    handle @shell {\n" +
            "        reverse_proxy webui:8080\n" +
            "    }\n" +
            Checked("", "webui:8080") +
            "}\n";

        var caddyfileBytes = Encoding.UTF8.GetBytes(testCaddyfile);

        // Probed at sign-in, the one page here nothing checks, which also proves the route to the
        // accounts service.
        _caddy = TestContainers.Container("caddy:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("caddy")
            .WithPortBinding(_caddyPort, 80)
            .WithResourceMapping(caddyfileBytes, "/etc/caddy/Caddyfile")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/accounts/sign-in")))
            .Build();
        await E2ETrace.TimeAsync("start:caddy", () => _caddy.StartAsync(ct));

        // Wait for the full stack to be reachable through Caddy, signed in: the hub's negotiate
        // answering a session proves Caddy -> session check -> mcp-channel-signalr end to end.
        var probe = _probe!;
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes(2));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var negotiate = new HttpRequestMessage(
                    HttpMethod.Post, $"{baseUrl}/hubs/chat/negotiate?negotiateVersion=1");
                negotiate.Headers.Add("Cookie", $"ziggurat_session={probe.Session}");
                var response = await httpClient.SendAsync(negotiate, ct);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                // ignore connection errors during startup
            }
            await Task.Delay(250, ct);
        }

        E2ETrace.Write("start:total", stackElapsed.Elapsed.TotalSeconds);
        SugaryUrl = $"http://{_mcpSugary.Hostname}:{_mcpSugary.GetMappedPublicPort(8080)}";
        SugaryMcpEndpoint = $"{SugaryUrl}/mcp";
        WebChatUrl = $"{baseUrl}/";
    }

    // The home household's agents, as the household migration seeds the deployment's: the agent's
    // settings carry none, and a household that saved none runs the two templates. Saved into the
    // seeded store, so the agent's first catalogue for the channels is these.
    public static readonly AgentsSection Agents = new()
    {
        DefaultAgent = "test-agent",
        Agents =
        [
            new HouseholdAgent
            {
                Id = "test-agent",
                Definition = new AgentDefinition
                {
                    Id = "test-agent",
                    Name = "Test Agent",
                    Model = "~deepseek/deepseek-v4-flash-latest:nitro",
                    McpServerEndpoints = ["http://mcp-vault:8080/mcp"],
                    WhitelistPatterns = ["__none__"],
                    // The model sometimes answers a plain greeting with a tool call, which raises an
                    // approval prompt no test asked for and leaks it onto every later page. The
                    // approval tests demand a tool call explicitly, so they still get one.
                    CustomInstructions =
                        "Use tools only when the message explicitly asks for an action that needs one; otherwise reply with plain text."
                }
            },
            // The attachment E2E needs a model that accepts image input: capability is discovered from
            // the provider and the composer refuses the send otherwise. Its own agent, because the
            // other tests are tuned against the default agent's model; no tool endpoints, so the reply
            // is always text and never an approval prompt.
            new HouseholdAgent
            {
                Id = "vision-agent",
                Definition = new AgentDefinition
                {
                    Id = "vision-agent",
                    Name = "Vision Agent",
                    Model = "google/gemini-3.1-flash-lite",
                    McpServerEndpoints = [],
                    WhitelistPatterns = ["__none__"]
                }
            },
            // Her carb-counting agent as the home defines it — model, effort, prompt server, features —
            // read from the tests' copies of the agents (TestAgents) rather than restated, so the
            // Sugary E2E estimates with the agent she uses.
            new HouseholdAgent { Id = "sugary", Definition = TestAgents.Repository.Agents.Single(a => a.Id == "sugary") }
        ]
    };

    // The household's spaces as its owner would save them: the default space, one per collection,
    // and the kitchen. Saved into the seeded store, before any page has asked the hub for them.
    private static SpacesSection Spaces => new()
    {
        Spaces =
        [
            HouseholdSpace.Default,
            .. _spaces.Select((slug, index) => new HouseholdSpace
            {
                Slug = slug, Name = slug, AccentColor = SpaceConfig.DefaultAccentColor, Order = index + 1
            }),
            Kitchen
        ]
    };

    // whisper-server's own route, answered in process. The channel container reaches it through
    // the host gateway, so nothing about the browser's path to the transcript is faked.
    private async Task<int> StartWhisperStubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.ListenAnyIP(0));
        builder.Logging.ClearProviders();
        _whisper = builder.Build();

        _whisper.MapPost("/v1/audio/transcriptions", async context =>
        {
            // Kept, so a test can assert on the format the browser really encoded.
            var form = await context.Request.ReadFormAsync();
            var space = "";
            if (form.Files.FirstOrDefault() is { } posted)
            {
                // dictation-<space>.wav, which the channel server names it. Answering by space is
                // what lets collections dictate at the same time without crossing answers.
                space = Path.GetFileNameWithoutExtension(posted.FileName ?? "")
                    .Replace("dictation-", "", StringComparison.Ordinal);
                using var buffer = new MemoryStream();
                await using var content = posted.OpenReadStream();
                await content.CopyToAsync(buffer);
                lock (_stub)
                {
                    _lastAudio[space] = buffer.ToArray();
                }
            }

            var status = StatusFor(space);
            if (status != 200)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsync("the transcriber is having a bad day");
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { text = TranscriptFor(space), language = "es" }));
        });

        await _whisper.StartAsync();
        var address = _whisper.Urls.First();
        return new Uri(address).Port;
    }

    private static string? GetOpenRouterApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER__APIKEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return envKey;
        }

        try
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets("bae64127-c00e-4499-8325-0fb6b452133c")
                .Build();
            return config["OpenRouter:ApiKey"];
        }
        catch
        {
            return null;
        }
    }

    // Torn down together, because every second of this is wall clock: the last test has finished
    // and the run is not over until the containers are gone. Stopping seven of them in a line was
    // several seconds of the total spent after anything was still being tested. Nothing here waits
    // on anything else here — only the network does, and it has to outlive what is attached to it.
    private async Task StopAsync()
    {
        await Task.WhenAll(
            _whisper is null ? Task.CompletedTask : _whisper.DisposeAsync().AsTask(),
            _keyProviders is null ? Task.CompletedTask : _keyProviders.DisposeAsync().AsTask(),
            _caddy is null ? Task.CompletedTask : _caddy.DisposeAsync().AsTask(),
            _mcpSugary is null ? Task.CompletedTask : _mcpSugary.DisposeAsync().AsTask(),
            _accounts is null ? Task.CompletedTask : _accounts.DisposeAsync().AsTask(),
            _webui is null ? Task.CompletedTask : _webui.DisposeAsync().AsTask(),
            _agent is null ? Task.CompletedTask : _agent.DisposeAsync().AsTask(),
            _mcpChannelSignalR is null ? Task.CompletedTask : _mcpChannelSignalR.DisposeAsync().AsTask(),
            _mcpVault is null ? Task.CompletedTask : _mcpVault.DisposeAsync().AsTask(),
            _redis is null ? Task.CompletedTask : _redis.DisposeAsync().AsTask());

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }

        if (_caddyPort != 0)
        {
            TestPort.Release(_caddyPort);
        }
    }
}