using Infrastructure.Clients.Bash;
using Shouldly;
using Tests.E2E.Fixtures;
using Xunit;

namespace Tests.Unit.Infrastructure;

public class CommandEnvironmentTests
{
    // Every key the deployment's secrets file declares, read from the file itself so a secret
    // added there later is covered without touching this test.
    private static IReadOnlyList<string> DeploymentSecretKeys()
    {
        var envFile = Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", ".env");
        return File.ReadAllLines(envFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2)[0])
            .ToList();
    }

    [Fact]
    public void Minimal_FromAServerHoldingEverySecret_PassesNoneOfThem()
    {
        var keys = DeploymentSecretKeys();
        keys.ShouldNotBeEmpty();
        var server = keys.ToDictionary(k => k, k => $"secret-{k}");
        server["PATH"] = "/home/sandbox_user/.local/bin:/usr/bin:/bin";
        server["TZ"] = "Europe/Madrid";

        var environment = CommandEnvironment.Minimal("/home/sandbox_user", server.GetValueOrDefault);

        environment.Keys.Intersect(keys.Where(k => k is not ("TZ" or "PATH" or "HOME"))).ShouldBeEmpty();
        environment.Values.ShouldNotContain(v => v.StartsWith("secret-"));
    }

    [Fact]
    public void Minimal_KeepsHomePathTimezoneAndLocale()
    {
        var server = new Dictionary<string, string>
        {
            ["HOME"] = "/root",
            ["PATH"] = "/home/sandbox_user/.local/bin:/usr/bin:/bin",
            ["TZ"] = "Europe/Madrid",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["OPENROUTER__APIKEY"] = "sk-leak"
        };

        var environment = CommandEnvironment.Minimal("/home/sandbox_user", server.GetValueOrDefault);

        environment.ShouldBe(new Dictionary<string, string>
        {
            ["HOME"] = "/home/sandbox_user",
            ["PATH"] = "/home/sandbox_user/.local/bin:/usr/bin:/bin",
            ["TZ"] = "Europe/Madrid",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8"
        }, ignoreOrder: true);
    }

    // A server started with no PATH of its own still hands its commands one a login shell can
    // start from.
    [Fact]
    public void Minimal_WithNoPathOnTheServer_FallsBackToTheSystemPath()
    {
        var environment = CommandEnvironment.Minimal("/home/sandbox_user", _ => null);

        environment["PATH"].ShouldBe(CommandEnvironment.DefaultPath);
        environment.ShouldNotContainKey("TZ");
    }
}