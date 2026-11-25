using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A check as the agent runs one: `./check` in an app's directory on the /apps mount, the preview loaded in
// the apps server's own headless Chromium as the person whose turn ran it, and what it saw read back from
// /apps/<slug>/.checks/latest/ with the file tools. Real Chromium, in process, on the fixture's apps
// domain (apps.test, no .localhost name), so the secure context the check gives the preview is its own.
public sealed class AppChecksTests
{
    private static readonly string[] _defaultShots = ["1280x800.png", "390x844.png"];

    [Fact]
    public async Task ACheck_ShowsThePreviewAtAPhoneAndADesktop_AndReportsEach()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            "<!doctype html><html lang=\"en\"><head><title>Glucose</title></head><body><h1>Glucose today</h1></body></html>");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check");

        ran["exitCode"]!.GetValue<int>().ShouldBe(0, ran.ToJsonString());
        ran["stdout"]!.GetValue<string>().ShouldContain("/glucose/.checks/latest/");
        (await server.GlobAsync(AppsServer.Her, "*", "/glucose/.checks/latest"))
            .ShouldBe([.. _defaultShots.Select(s => $"/glucose/.checks/latest/{s}"), "/glucose/.checks/latest/report.json"]);
        foreach (var shot in _defaultShots)
        {
            (await BytesAsync(server, $"/glucose/.checks/latest/{shot}"))[..8]
                .ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, shot);
        }

        var report = await ReportAsync(server, "glucose");
        report["deadlineHit"]!.GetValue<bool>().ShouldBeFalse();
        var viewports = report["viewports"]!.AsArray();
        viewports.Select(v => v!["screenshot"]!.GetValue<string>()).ShouldBe(["390x844.png", "1280x800.png"]);
        viewports.ShouldAllBe(v => v!["accessibility"]!.GetValue<string>().Contains("heading \"Glucose today\" [level=1]"));
    }

    private static async Task<JsonNode> ReportAsync(AppsServer server, string slug) =>
        JsonNode.Parse(await BytesAsync(server, $"/{slug}/.checks/latest/report.json"))!;

    // A file of the mount whole, as a copy streams it: the byte read, a chunk at a time.
    private static async Task<byte[]> BytesAsync(AppsServer server, string path)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var chunk = await server.CallAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = path, ["offset"] = bytes.Count, ["length"] = 256 * 1024
            }, AppsServer.TurnOf(AppsServer.Her));
            var read = Convert.FromBase64String(chunk["contentBase64"]?.GetValue<string>()
                                                ?? throw new InvalidOperationException(chunk.ToJsonString()));
            bytes.AddRange(read);
            if (chunk["eof"]!.GetValue<bool>() || read.Length == 0)
            {
                return [.. bytes];
            }
        }
    }
}