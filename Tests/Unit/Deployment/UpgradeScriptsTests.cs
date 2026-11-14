using System.Diagnostics;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Deployment;

// The upgrade scripts read a deployment's own .env, which an operator wrote by hand over the years:
// its names are whatever .NET's configuration accepted (case-insensitive, and the server's own key
// rather than the tracked template's), not the spelling of the file they are carried into.
public sealed class UpgradeScriptsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("upgrade-scripts-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task HomeKeys_ReadsIdealistasSecretUnderTheNameMastersServerRead()
    {
        // Master's Idealista server bound Idealista:ApiSecret; its tracked .env said IDEALISTA__SECRET,
        // which bound nothing, so a deployment that worked spelt it IDEALISTA__APISECRET.
        var old = Write("old.env", "IDEALISTA__APIKEY=the-key\nIDEALISTA__APISECRET=the-secret\n");

        var (exit, output) = await RunAsync("home-keys.sh", old);

        exit.ShouldBe(0);
        output.ShouldContain("\"apiKey\": \"the-key\"");
        output.ShouldContain("\"secret\": \"the-secret\"");
    }

    [Fact]
    public async Task CarryEnv_CarriesAValueWhoseOldNameDiffersOnlyInCase()
    {
        var old = Write("old.env", "ANNOUNCE__TOKEN=announce\nLEMONADECHAT__APIURL=http://box:13305/api/v1\n");
        var fresh = Write("new.env", "Announce__Token=\nLemonadeChat__ApiUrl=\nLemonadeChat__ApiKey=\n");

        var (exit, output) = await RunAsync("carry-env.sh", old, fresh);

        exit.ShouldBe(0);
        var carried = await File.ReadAllTextAsync(fresh);
        carried.ShouldContain("Announce__Token=announce\n");
        carried.ShouldContain("LemonadeChat__ApiUrl=http://box:13305/api/v1\n");
        output.ShouldContain("still empty: LemonadeChat__ApiKey");
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string script, params string[] args)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(TestHelpers.FindSolutionRoot(), "scripts", "upgrade", script));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}