using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sandbox;

// The prompt the deployment actually serves, which is a different claim from the one the
// conformance test makes about the text: the prompt is now built from an injected filesystem, so
// it is constructed per request by the MCP host, and nothing below the wire exercises that. What it
// names is then checked against the running container — the directory the agent is told to work in
// is one it can write.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxPromptE2ETests(SandboxE2EFixture fixture)
{
    [SkippableFact]
    public async Task ThePromptTheServerServes_NamesAWorkspaceThatExistsAndIsWritable()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var prompt = await client.GetPromptAsync("sandbox_prompt", cancellationToken: cts.Token);
        var text = string.Join("\n", prompt.Messages
            .Select(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        var workspace = await client.ReadResourceAsync("filesystem://sandbox", cancellationToken: cts.Token);
        var published = JsonDocument.Parse(string.Join("", workspace.Contents
            .OfType<TextResourceContents>()
            .Select(c => c.Text))).RootElement;
        var declared = published.GetProperty("mountPoint").GetString()
            + "/" + published.GetProperty("workspace").GetString();

        text.ShouldContain(declared);

        // And it is a directory the container user can write, which is the whole claim the prompt
        // makes about it.
        var written = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = $"touch {declared}/prompt-probe && rm {declared}/prompt-probe"
        }, cancellationToken: cts.Token);

        JsonDocument.Parse(string.Join("\n", written.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
    }
}