using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sandbox;

// The prompt tells the agent where to work, and here the directory it names either exists in the
// image or does not. Nothing lower can say that: every in-process fixture builds the server against
// a temporary root, so a prompt naming a directory nobody creates reads the same as one naming a
// directory that is there.
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