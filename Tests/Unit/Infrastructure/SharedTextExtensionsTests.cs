using Domain.DTOs.FileSystem;
using Domain.Tools.Files;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.Infrastructure;

// What counts as text is one list, in Domain, so two filesystem servers cannot disagree about a
// file the agent can read. A server may still hand its backend a list of its own — the outpost's
// --ext does exactly that for one machine — but nobody carries a second copy of the default.
public class SharedTextExtensionsTests
{
    // Drives the shipped sandbox module rather than constructing a backend, because the thing at
    // risk is the wiring: a module that kept reading a settings copy would stay green against a
    // shared definition nothing consumes. The refusal names the list it was given, so a disallowed
    // extension is the seam that reports which list the backend really holds.
    [Fact]
    public async Task TheSandboxServer_RefusesUsingTheSharedTextExtensionList()
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get("sandbox").Configure(services);
        await using var provider = services.BuildServiceProvider();

        var backend = provider.GetRequiredService<SandboxFileSystem>();
        var refusal = await backend.CreateAsync(
            "notes.unreadable", "content", overwrite: false, createDirectories: false, CancellationToken.None);

        refusal.ShouldBeOfType<FsResult<FsCreateResult>.Err>()
            .Error.Message.ShouldContain(string.Join(", ", TextFileExtensions.Default));
    }

    // The list the sandbox shipped, so moving it into Domain did not quietly change what the agent
    // can read. The empty entry is the one worth naming: it is how an extensionless file — a
    // Dockerfile, a Makefile — stays readable, and it is the first thing a hand-written list drops.
    [Fact]
    public void TheSharedList_CarriesWhatTheSandboxShipped()
    {
        TextFileExtensions.Default.ShouldContain("");
        TextFileExtensions.Default.ShouldContain(".md");
        TextFileExtensions.Default.ShouldContain(".py");
        TextFileExtensions.Default.ShouldContain(".rs");
        TextFileExtensions.Default.ShouldContain(".ipynb");
    }
}