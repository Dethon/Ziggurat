using System.Diagnostics;
using System.Net;
using Accounts;
using Accounts.Commands;
using Accounts.Settings;
using Microsoft.Data.Sqlite;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The container's verbs: the operator's way in before anyone can sign in and back in after losing
// every passkey, a recovery link for anyone, and a session for a seeded person.
public class AccountsCommandTests
{
    [Fact]
    public async Task MintLink_Operator_CreatesTheOperatorOnceAndRecoversThemAfter()
    {
        await using var server = await AccountsServer.StartAsync();

        var (firstExit, first) = await server.RunAsync("mint-link", "--operator", "--name", "Fran");
        var (againExit, again) = await server.RunAsync("mint-link", "--operator");

        firstExit.ShouldBe(0);
        first.ShouldContain("Created the operator, Fran");
        againExit.ShouldBe(0);
        again.ShouldNotContain("Created");
        var created = AccountsServer.MintedLink.Parse(first);
        var recovered = AccountsServer.MintedLink.Parse(again);
        recovered.UserId.ShouldBe(created.UserId);
        recovered.TenantId.ShouldBe(created.TenantId);
        recovered.Url.ShouldNotBe(created.Url);
    }

    [Fact]
    public async Task MintLink_Operator_PrintsALinkOnThePublicBaseThatOpensTheEnrollmentPage()
    {
        await using var server = await AccountsServer.StartAsync();

        var link = await server.MintOperatorAsync("Fran");

        link.Url.GetLeftPart(UriPartial.Authority).ShouldBe(server.Settings.PublicBaseUrl);
        var page = await server.Client().GetAsync(link.Url);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("Fran");
    }

    [Fact]
    public async Task MintLink_OperatorWithNoNameBeforeTheyExist_MintsNothing()
    {
        await using var server = await AccountsServer.StartAsync();

        var (exit, output) = await server.RunAsync("mint-link", "--operator");

        exit.ShouldBe(2);
        output.ShouldContain("needs a display name");
        output.ShouldNotContain("http");
    }

    [Fact]
    public async Task MintLink_User_PrintsALinkForThatPerson()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var (exit, output) = await server.RunAsync("mint-link", "--user", laura);

        exit.ShouldBe(0);
        AccountsServer.MintedLink.Parse(output).UserId.ShouldBe(laura);
        output.ShouldContain("Laura");
    }

    // The operator's way to add someone before the members page exists, and the test stack's way
    // to seed people it then signs in without a ceremony.
    [Fact]
    public async Task CreateUser_AddsAMemberOfTheHomeHouseholdAndPrintsWhoTheyAre()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var (exit, output) = await server.RunAsync("create-user", "--name", "Laura");

        exit.ShouldBe(0, output);
        output.ShouldContain("Laura");
        var laura = AccountsServer.ParseWho(output);
        laura.TenantId.ShouldBe(fran.TenantId);
        laura.UserId.ShouldNotBe(fran.UserId);
        var check = await server.VerifyAsync(await server.MintSessionAsync(laura.UserId));
        check.Headers.GetValues("X-Ziggurat-User").ShouldBe([laura.UserId]);
        check.Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["member"]);
    }

    [Theory]
    [InlineData("member", "member")]
    [InlineData("owner", "owner")]
    public async Task CreateUser_WithARole_GivesThemThatRole(string role, string roles)
    {
        await using var server = await AccountsServer.StartAsync();

        var (exit, output) = await server.RunAsync("create-user", "--name", "Laura", "--role", role);

        exit.ShouldBe(0, output);
        var laura = AccountsServer.ParseWho(output);
        var check = await server.VerifyAsync(await server.MintSessionAsync(laura.UserId));
        check.Headers.GetValues("X-Ziggurat-Roles").ShouldBe([roles]);
    }

    [Theory]
    [InlineData("create-user")]
    [InlineData("create-user", "--name")]
    [InlineData("create-user", "--name", " ")]
    [InlineData("create-user", "--name", "Laura", "--role", "operator")]
    public async Task CreateUser_UnreadableArguments_CreatesNobody(params string[] args)
    {
        await using var server = await AccountsServer.StartAsync();

        var (exit, output) = await server.RunAsync(args);

        exit.ShouldBe(2);
        output.ShouldContain("usage:");
    }

    [Theory]
    [InlineData("mint-link")]
    [InlineData("mint-session")]
    public async Task Run_UnknownUser_MintsNothing(string verb)
    {
        await using var server = await AccountsServer.StartAsync();

        var (exit, output) = await server.RunAsync(verb, "--user", "0123456789abcdef0123456789abcdef");

        exit.ShouldBe(1);
        output.ShouldContain("there is no user");
    }

    [Theory]
    [InlineData("mint-link")]
    [InlineData("mint-link", "--user")]
    [InlineData("mint-session", "--operator")]
    public async Task Run_UnreadableArguments_PrintsUsage(params string[] args)
    {
        await using var server = await AccountsServer.StartAsync();

        var (exit, output) = await server.RunAsync(args);

        exit.ShouldBe(2);
        output.ShouldStartWith("usage:");
    }

    // The operator's first command may come before the service has ever started on the volume.
    [Fact]
    public async Task MintLink_OnAFreshVolume_CreatesTheStoreAndTheHomeHouseholdFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), "accounts-tests", Guid.NewGuid().ToString("N"));
        var settings = new AccountsSettings
        {
            DatabasePath = Path.Combine(directory, "store", "accounts.db"),
            PublicBaseUrl = "https://assistants.example",
            AllowedOrigins = ["https://assistants.example"],
            WebAuthn = new WebAuthnSettings { RelyingPartyId = "example" }
        };
        try
        {
            await using var output = new StringWriter();
            var exit = await AccountsCommand.RunAsync(
                ["mint-link", "--operator", "--name", "Fran"], settings, new AccountsClock(TimeProvider.System), output,
                CancellationToken.None);

            exit.ShouldBe(0, output.ToString());
            AccountsServer.MintedLink.Parse(output.ToString()).TenantId.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    // `docker exec accounts dotnet Accounts.dll mint-link ...` is the shipped binary run with
    // arguments: it has to do its work and exit, never stay up serving beside the real server.
    [Fact]
    public async Task Run_AsTheShippedBinary_PrintsAndExitsWithoutServing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "accounts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = TestPort.GetAvailable();
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "Accounts.dll"), "mint-link", "--operator", "--name", "Fran" },
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["DATABASEPATH"] = Path.Combine(directory, "accounts.db"),
                ["PUBLICBASEURL"] = "https://assistants.example",
                ["ALLOWEDORIGINS__0"] = "https://assistants.example",
                ["WEBAUTHN__RELYINGPARTYID"] = "example",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}"
            }
        };
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new ShouldAssertException("the command was still running after a minute: it is serving");
            }

            process.ExitCode.ShouldBe(0, await error);
            AccountsServer.MintedLink.Parse(await output).Url.Host.ShouldBe("assistants.example");
        }
        finally
        {
            TestPort.Release(port);
            Directory.Delete(directory, recursive: true);
        }
    }
}