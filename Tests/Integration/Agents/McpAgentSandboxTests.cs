using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentSandboxTests(McpSandboxServerFixture fixture) : IClassFixture<McpSandboxServerFixture>
{
    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var transport = McpTestSecret.Transport(fixture.McpEndpoint);
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static JsonDocument ParseToolJson(CallToolResult result)
    {
        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));
        return JsonDocument.Parse(text);
    }

    [SkippableFact]
    public async Task Exec_PythonInline_ReturnsExpectedOutput()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = "python3 -c 'print(2+2)'"
        }, cancellationToken: cts.Token);

        result.IsError.ShouldNotBe(true);
        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
        json.RootElement.GetProperty("stdout").GetString()!.Trim().ShouldBe("4");
    }

    [SkippableFact]
    public async Task Exec_RoundTripWithFsCreate_RunsAgentWrittenFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cts.Token);

        var relHome = Path.GetRelativePath("/", fixture.HomeDir);

        await client.CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = Path.Combine(relHome, "hello.py"),
            ["content"] = "print('hi from agent')",
            ["overwrite"] = true,
            ["createDirectories"] = true
        }, cancellationToken: cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = relHome,
            ["command"] = "python3 hello.py"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
        json.RootElement.GetProperty("stdout").GetString()!.Trim().ShouldBe("hi from agent");
    }

    [SkippableFact]
    public async Task Exec_NonZeroExit_ReturnedAsData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = "false"
        }, cancellationToken: cts.Token);

        result.IsError.ShouldNotBe(true);
        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(1);
    }

    [SkippableFact]
    public async Task Exec_Timeout_TruncatesAndKills()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = "sleep 30",
            ["timeoutSeconds"] = 1
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("timedOut").GetBoolean().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task Exec_OutputExceedsCap_TruncatedReported()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = "yes a | head -c 200000"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task Exec_NonExistentCwd_ReturnsErrorJson()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "this/does/not/exist",
            ["command"] = "echo hi"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    [SkippableFact]
    public async Task ListResources_ExposesSandboxFilesystem()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var resources = await client.ListResourcesAsync(cancellationToken: cts.Token);

        resources.Any(r => r.Uri == "filesystem://sandbox").ShouldBeTrue();
    }
}