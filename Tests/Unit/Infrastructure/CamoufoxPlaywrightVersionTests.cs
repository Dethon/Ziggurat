using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Infrastructure;

// The browser runs in the Camoufox sidecar, and the .NET client connects to it over Playwright's
// wire protocol, which refuses any client whose major.minor differs from the server's (428
// "Playwright version mismatch"). The client version is a NuGet reference and the server's is a
// pair of strings in a Dockerfile, so a routine dependency bump moves one and not the other, and
// every web_browse in production fails while web_search keeps working.
public partial class CamoufoxPlaywrightVersionTests
{
    [Fact]
    public void TheCamoufoxSidecar_RunsThePlaywrightMinorTheClientSpeaks()
    {
        var root = TestHelpers.FindSolutionRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "Infrastructure", "Infrastructure.csproj"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "DockerCompose", "camoufox", "Dockerfile"));

        var client = MinorOf(ClientVersion().Match(csproj));
        var baseImage = MinorOf(BaseImageVersion().Match(dockerfile));
        var core = MinorOf(CoreVersion().Match(dockerfile));

        baseImage.ShouldBe(client, "the camoufox base image must ship the Playwright minor the .NET client speaks");
        core.ShouldBe(client, "the camoufox playwright-core must be the Playwright minor the .NET client speaks");
    }

    private static string MinorOf(Match match)
    {
        match.Success.ShouldBeTrue("a Playwright version pin was not found where this test expects it");
        return $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
    }

    [GeneratedRegex("""Include="Microsoft\.Playwright" Version="(?<major>\d+)\.(?<minor>\d+)""")]
    private static partial Regex ClientVersion();

    [GeneratedRegex(@"FROM mcr\.microsoft\.com/playwright:v(?<major>\d+)\.(?<minor>\d+)")]
    private static partial Regex BaseImageVersion();

    [GeneratedRegex(@"playwright-core@(?<major>\d+)\.(?<minor>\d+)")]
    private static partial Regex CoreVersion();
}