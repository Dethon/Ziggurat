using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// What a built app's page may load and reach: its own origin, from the fixed CDN list the packages its
// pages were found pinned with for code, styles and fonts, and the outside origins its manifest names for
// fetches and images. Nothing else, no frame around it, no base, no form posting anywhere.
public class AppContentPolicyTests
{
    private static readonly IReadOnlyList<string> _cdns = ["https://cdn.jsdelivr.net", "https://unpkg.com"];

    private static AppManifest Manifest(params string[] connect) => new()
    {
        Name = "Glucose today", AccentColor = "#e9601f", Description = "My glucose today.", Connect = connect
    };

    [Fact]
    public void AnAppPinningNothingAndConnectingNowhere_RunsItsOwnCodeAlone() =>
        AppContentPolicy.For(Manifest(), _cdns).ShouldBe(
            "default-src 'none'; "
            + "script-src 'self'; "
            + "style-src 'self' 'unsafe-inline'; "
            + "font-src 'self'; "
            + "img-src 'self' data: blob: https://cdn.jsdelivr.net https://unpkg.com; "
            + "connect-src 'self'; "
            + "manifest-src 'self'; "
            + "worker-src 'self'; "
            + "frame-ancestors 'none'; "
            + "base-uri 'none'; "
            + "form-action 'none'");

    [Fact]
    public void WhatTheManifestConnectsTo_IsFetchedAndShown_ButNeverRunAsCode()
    {
        var policy = Directives(AppContentPolicy.For(Manifest("https://api.example.org", "https://tiles.example.net:8443"), _cdns));

        policy["connect-src"].ShouldBe("'self' https://api.example.org https://tiles.example.net:8443");
        policy["img-src"].ShouldBe(
            "'self' data: blob: https://cdn.jsdelivr.net https://unpkg.com https://api.example.org https://tiles.example.net:8443");
        policy["script-src"].ShouldNotContain("example");
        policy["style-src"].ShouldNotContain("example");
        policy["font-src"].ShouldNotContain("example");
    }

    // A page's own inline import maps run by their hashes, and nothing else inline does.
    [Fact]
    public void APagesImportMapHashes_AreTheOnlyInlineScriptItRuns()
    {
        var policy = Directives(AppContentPolicy.For(Manifest(), _cdns, scriptHashes: ["sha256-abc=", "sha256-def="]));

        policy["script-src"].ShouldBe("'self' 'sha256-abc=' 'sha256-def='");
        policy["style-src"].ShouldNotContain("sha256");
    }

    // What was checked is what runs: a pinned script or module lets its package's version directory load (a
    // bundle's chunks, an esm.sh stub's build), and the address itself where the version ends it; a pinned
    // stylesheet its directory for styles and fonts (its fonts load by relative path). Never a CDN whole.
    [Fact]
    public void PinnedCode_LoadsItsPackagesVersionDirectory_NeverTheWholeCdn()
    {
        var policy = Directives(AppContentPolicy.For(Manifest(), _cdns, new PinnedCode
        {
            Scripts = ["https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.js", "https://unpkg.com/htmx.org@1.9.10?module",
                "https://cdn.jsdelivr.net/npm/@kurkle/color@0.3.2/+esm"],
            Styles = ["https://unpkg.com/katex@0.16.9/dist/katex.min.css"]
        }));

        policy["script-src"].ShouldBe("'self' https://cdn.jsdelivr.net/npm/chart.js@4.4.1/ https://unpkg.com/htmx.org@1.9.10/ "
                                      + "https://unpkg.com/htmx.org@1.9.10 https://cdn.jsdelivr.net/npm/@kurkle/color@0.3.2/");
        policy["style-src"].ShouldBe("'self' 'unsafe-inline' https://unpkg.com/katex@0.16.9/");
        policy["font-src"].ShouldBe("'self' https://unpkg.com/katex@0.16.9/");
        policy["img-src"].ShouldBe("'self' data: blob: https://cdn.jsdelivr.net https://unpkg.com");
    }

    [Fact]
    public void PinnedGitHubCode_LoadsItsCommitsDirectory()
    {
        var policy = Directives(AppContentPolicy.For(Manifest(), _cdns, new PinnedCode
        {
            Scripts = ["https://cdn.jsdelivr.net/gh/someone/lib@0123456789abcdef0123456789abcdef01234567/dist/lib.js"]
        }));

        policy["script-src"].ShouldBe("'self' https://cdn.jsdelivr.net/gh/someone/lib@0123456789abcdef0123456789abcdef01234567/");
    }

    // The addresses are the agent's text: one off the CDN list, or one a policy could not carry whole (a
    // `;` or `,` ending the directive or the source, a quote, a space), lets nothing load.
    [Theory]
    [InlineData("https://evil.example/npm/x@1.0.0/x.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/x@1.0.0/x.js;script-src *")]
    [InlineData("https://cdn.jsdelivr.net/npm/x@1.0.0/a,b.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/x@1.0.0/'unsafe-eval'.js")]
    [InlineData("javascript:alert(1)")]
    public void APinnedAddressOffTheCdnsOrThatAPolicyCannotCarry_LoadsNothing(string address)
    {
        var policy = Directives(AppContentPolicy.For(Manifest(), _cdns, new PinnedCode { Scripts = [address], Styles = [address] }));

        policy["script-src"].ShouldBe("'self'");
        policy["style-src"].ShouldBe("'self' 'unsafe-inline'");
        policy.Keys.Count.ShouldBe(11);
    }

    // An answer that is no app's (an unknown label, a removed app's page): nothing loads but itself.
    [Fact]
    public void NoAppsAnswer_LoadsNothingFromAnywhere() =>
        AppContentPolicy.Closed.ShouldBe(
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");

    private static Dictionary<string, string> Directives(string policy) =>
        policy.Split("; ").Select(d => d.Split(' ', 2)).ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : "");
}