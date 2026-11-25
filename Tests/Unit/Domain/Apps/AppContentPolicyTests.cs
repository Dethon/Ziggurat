using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// What a built app's page may load and reach: its own origin, the fixed CDN list for code, styles and
// fonts, and the outside origins its manifest names for fetches and images. Nothing else, no frame
// around it, no base, no form posting anywhere.
public class AppContentPolicyTests
{
    private static readonly IReadOnlyList<string> _cdns = ["https://cdn.jsdelivr.net", "https://unpkg.com"];

    private static AppManifest Manifest(params string[] connect) => new()
    {
        Name = "Glucose today", AccentColor = "#e9601f", Description = "My glucose today.", Connect = connect
    };

    [Fact]
    public void AnAppConnectingNowhere_LoadsFromItselfAndTheCdnsAlone() =>
        AppContentPolicy.For(Manifest(), _cdns).ShouldBe(
            "default-src 'none'; "
            + "script-src 'self' https://cdn.jsdelivr.net https://unpkg.com; "
            + "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com; "
            + "font-src 'self' https://cdn.jsdelivr.net https://unpkg.com; "
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
        var policy = Directives(AppContentPolicy.For(Manifest(), _cdns, ["sha256-abc=", "sha256-def="]));

        policy["script-src"].ShouldBe("'self' 'sha256-abc=' 'sha256-def=' https://cdn.jsdelivr.net https://unpkg.com");
        policy["style-src"].ShouldNotContain("sha256");
    }

    // An answer that is no app's (an unknown label, a removed app's page): nothing loads but itself.
    [Fact]
    public void NoAppsAnswer_LoadsNothingFromAnywhere() =>
        AppContentPolicy.Closed.ShouldBe(
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");

    private static Dictionary<string, string> Directives(string policy) =>
        policy.Split("; ").Select(d => d.Split(' ', 2)).ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : "");
}