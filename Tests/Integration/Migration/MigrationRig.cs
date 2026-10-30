using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using HouseholdMigration;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;
using SugarySettings = McpServerSugary.Settings.SugarySettings;

namespace Tests.Integration.Migration;

// Everything the migration reaches in the stack, stood up for one test: the accounts service in this
// process, admitting the migration's credential; the class's Redis; a Sugary log and a voices folder
// in a directory of the test's own. The run is the container's command, handed the settings its
// compose service would be.
public sealed class MigrationRig : IAsyncDisposable
{
    public const string Credential = "migration-credential";
    private const string TelegramCredential = "telegram-credential";

    // The browse server's key: what the old records were sealed with and the new ones will be.
    public static readonly byte[] StateKey = RandomNumberGenerator.GetBytes(32);

    private readonly ServiceProvider _sugary;

    private MigrationRig(AccountsServer accounts, string redis, int memoryDimension, string directory, ServiceProvider sugary)
    {
        Accounts = accounts;
        Directory = directory;
        _sugary = sugary;
        Settings = new MigrationSettings
        {
            RedisConnectionString = redis,
            Accounts = new AccountsEndpoint { BaseUrl = accounts.Address.ToString(), Credential = Credential },
            Logins = new LoginsSettings { StateKey = Convert.ToBase64String(StateKey) },
            SugaryDatabasePath = Path.Combine(directory, "sugary", "sugary.db"),
            VoicesPath = Path.Combine(directory, "voices"),
            MemoryDimension = memoryDimension
        };
    }

    public AccountsServer Accounts { get; }

    public string Directory { get; }

    public MigrationSettings Settings { get; }

    public string Voices => Settings.VoicesPath;

    public IDbContextFactory<SugaryDbContext> Sugary => _sugary.GetRequiredService<IDbContextFactory<SugaryDbContext>>();

    public SugaryDbContext SugaryOf(Account person) => _sugary.GetRequiredService<SugaryStore>().For(person);

    public static async Task<MigrationRig> StartAsync(RedisFixture redis, int memoryDimension)
    {
        var accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["household-migration"] = Credential, ["mcp-channel-telegram"] = TelegramCredential
            }
        });
        var directory = Path.Combine(Path.GetTempPath(), "household-migration-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "voices"));
        var sugary = new ServiceCollection()
            .AddSugaryStore(new SugarySettings { DatabasePath = Path.Combine(directory, "sugary", "sugary.db") })
            .BuildServiceProvider();
        await SugaryDatabase.MigrateAsync(sugary.GetRequiredService<IDbContextFactory<SugaryDbContext>>(), CancellationToken.None);
        return new MigrationRig(accounts, redis.ConnectionString, memoryDimension, directory, sugary);
    }

    // The run as the operator types it: the people's file, then the arguments.
    public async Task<(int Exit, string Output)> RunAsync(object people, params string[] args)
    {
        var file = Path.Combine(Directory, $"people-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { people }));
        await using var output = new StringWriter();
        var exit = await HouseholdMigrationCommand.RunAsync(
            ["--people", file, .. args], Settings, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    // Whose account a Telegram id is, asked the way the bot asks.
    public async Task<Account> AccountOfAsync(long telegramId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/internal/resolve")
        {
            Content = JsonContent.Create(new { channel = "telegram", identity = $"{telegramId}" })
        };
        request.Headers.Add("Authorization", $"Bearer {TelegramCredential}");
        var response = await Accounts.Client().SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"nobody holds Telegram {telegramId}");
        var resolved = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return new Account(resolved["tenantId"]!.GetValue<string>(), resolved["userId"]!.GetValue<string>());
    }

    // What the account's own session is told about them.
    public async Task<JsonObject> MeAsync(Account account)
    {
        var page = Accounts.SignedIn(await Accounts.MintSessionAsync(account.UserId));
        return JsonNode.Parse(await (await page.GetAsync("/accounts/me")).Content.ReadAsStringAsync())!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        await Accounts.DisposeAsync();
        await _sugary.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}