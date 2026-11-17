using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit;

// A bare `dotnet test` spends nothing but the two canaries: a test reaches a paid API only with the
// developer's key, and the key lives in user secrets, so a test outside the tiers that are paid on
// purpose — the canaries, the armed eval, the smoke stack — never reads them. A test that needs a
// model talks to FakeOpenRouter; one that needs a search engine answers its client with a canned page.
public partial class PaidCallsConformanceTests
{
    private static readonly string[] _paidOnPurpose =
    [
        Path.Combine("Tests", "Canary") + Path.DirectorySeparatorChar,
        Path.Combine("Tests", "Eval") + Path.DirectorySeparatorChar,
        Path.Combine("Tests", "Smoke") + Path.DirectorySeparatorChar
    ];

    [Fact]
    public void NoTestOutsideThePaidTiers_ReadsTheDevelopersKeys()
    {
        var root = TestHelpers.FindSolutionRoot();
        var tests = Path.Combine(root, "Tests");

        var offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(file => !_paidOnPurpose.Any(file.StartsWith))
            .Where(file => file != Path.Combine("Tests", "Unit", nameof(PaidCallsConformanceTests) + ".cs"))
            .Where(file => ReadsTheKeys().IsMatch(File.ReadAllText(Path.Combine(root, file))))
            .ToList();

        offenders.ShouldBeEmpty(
            "These read the developer's keys outside the canaries, the eval and the smoke tier, so a bare " +
            "`dotnet test` would pay for them. Point them at FakeOpenRouter, or move them into Tests/Eval.");
    }

    // The user-secrets store (by type or by its id) and the variable the deployment passes its key in.
    [GeneratedRegex(@"AddUserSecrets|bae64127-c00e-4499-8325-0fb6b452133c|EvalGate\.ApiKey|""OPENROUTER__APIKEY""\)")]
    private static partial Regex ReadsTheKeys();
}