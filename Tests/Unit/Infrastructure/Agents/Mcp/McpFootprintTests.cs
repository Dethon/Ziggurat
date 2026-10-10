using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Agents.Mcp;
using McpServerWebSearch.McpTools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.Infrastructure.Agents.Mcp;

// What a tool server says a call to one of its tools touches, read off the tool definition the
// server really publishes — the browse tools' own attributes — so the declaration and its
// reading cannot drift apart across the wire.
public class McpFootprintTests
{
    private static readonly Dictionary<string, object?> _none = [];

    // The race this exists for: two buttons on one tab, each navigating away, written in one
    // message — and a click beside a `back`, which is the same tool.
    [Fact]
    public void TwoActions_Conflict() =>
        Of<McpWebActionTool>(Call(("ref", "e-1"), ("action", "Click")))
            .ConflictsWith(Of<McpWebActionTool>(Call(("ref", "e-3"), ("action", "Click")))).ShouldBeTrue();

    [Fact]
    public void ASnapshotOrAPictureFetch_ConflictsWithAnAction()
    {
        var action = Of<McpWebActionTool>(Call(("action", "Back")));

        Of<McpWebSnapshotTool>(_none).ConflictsWith(action).ShouldBeTrue();
        Of<McpViewImageTool>(Call(("refs", new[] { "i-1" }))).ConflictsWith(action).ShouldBeTrue();
    }

    [Fact]
    public void ABrowse_ConflictsWithAnActionBesideIt() =>
        Of<McpWebBrowseTool>(Call(("url", "https://example.com/archive")))
            .ConflictsWith(Of<McpWebActionTool>(Call(("ref", "e-1"), ("action", "Click")))).ShouldBeTrue();

    // Different urls land on different tabs and the pool was built to load them at once; the same
    // url twice reloads one tab.
    [Fact]
    public void BrowsesOfDifferentPages_DoNotConflict_AndTwoOfTheSamePageDo()
    {
        var archive = Of<McpWebBrowseTool>(Call(("url", "https://example.com/archive")));

        archive.ConflictsWith(Of<McpWebBrowseTool>(Call(("url", "https://example.com/edition/7")))).ShouldBeFalse();
        archive.ConflictsWith(Of<McpWebBrowseTool>(Call(("url", "https://example.com/archive"), ("offset", 10000)))).ShouldBeTrue();
    }

    [Fact]
    public void ASearch_ConflictsWithNoBrowserCall_NorWithAnotherSearch()
    {
        var search = Of<McpWebSearchTool>(Call(("query", "pasta")));

        search.ConflictsWith(Of<McpWebSearchTool>(Call(("query", "eggs")))).ShouldBeFalse();
        search.ConflictsWith(Of<McpWebBrowseTool>(Call(("url", "https://example.com")))).ShouldBeFalse();
        search.ConflictsWith(Of<McpWebActionTool>(Call(("action", "Back")))).ShouldBeFalse();
    }

    [Fact]
    public void NoBrowserCall_ConflictsWithAFilesystemCall() =>
        Of<McpWebActionTool>(Call(("action", "Back")))
            .ConflictsWith(CallFootprint.Changing(SharedResource.Of("filesystem", "/vault"))).ShouldBeFalse();

    // The protocol's own hint, for a server that declares nothing richer: it reads its own
    // server's state, so it runs beside anything but a tool that said nothing at all.
    [Fact]
    public void AToolHintedReadOnly_ReadsItsOwnServer()
    {
        var hinted = McpFootprint.Of("library", new Tool { Name = "lookup", Annotations = new ToolAnnotations { ReadOnlyHint = true } })
            .ShouldNotBeNull().For(_none);

        hinted.ConflictsWith(hinted).ShouldBeFalse();
        hinted.ConflictsWith(CallFootprint.Changing(SharedResource.Of("filesystem", "/vault"))).ShouldBeFalse();
        hinted.ConflictsWith(CallFootprint.Everything).ShouldBeTrue();
    }

    [Fact]
    public void AToolThatDeclaresNothing_HasNoFootprint_AndIsOrderedAgainstEverything()
    {
        McpFootprint.Of("library", new Tool { Name = "download" }).ShouldBeNull();
        McpFootprint.Of("library", new Tool { Name = "download", Annotations = new ToolAnnotations { ReadOnlyHint = false } })
            .ShouldBeNull();
    }

    // The declaration is only worth anything if it survives the listing: over a real connection,
    // through the wrapper the agent offers the model, the browse tools still say what they said.
    [Fact]
    public async Task OverTheWire_TheAgentsOwnWrapperCarriesWhatTheServerDeclared()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpWebBrowseTool>()
            .WithTools<McpWebActionTool>()
            .WithTools<McpWebSearchTool>()
            .WithTools<FailingTools>());

        var offered = (await server.Client.ListToolsAsync())
            .ToDictionary(tool => tool.Name, tool => (AIFunction)new QualifiedMcpTool("websearch", tool));
        CallFootprint Footprint(string tool, IReadOnlyDictionary<string, object?> call) =>
            FootprintDeclaration.Of(offered[tool], call);

        var archive = Footprint(WebBrowseTool.Name, Call(("url", "https://example.com/archive")));
        var click = Footprint(WebActionTool.Name, Call(("ref", "e-1"), ("action", "Click")));
        var search = Footprint(WebSearchTool.Name, Call(("query", "pasta")));

        click.ConflictsWith(click).ShouldBeTrue();
        archive.ConflictsWith(click).ShouldBeTrue();
        archive.ConflictsWith(Footprint(WebBrowseTool.Name, Call(("url", "https://example.com/edition/7")))).ShouldBeFalse();
        search.ConflictsWith(click).ShouldBeFalse();
        search.ConflictsWith(search).ShouldBeFalse();
        // And a tool that declared nothing is still ordered against all of them.
        Footprint("throws", _none).ConflictsWith(search).ShouldBeTrue();
    }

    private static CallFootprint Of<TTool>(IReadOnlyDictionary<string, object?> call)
    {
        var published = McpServerTool.Create(
            typeof(TTool).GetMethod("Run")!,
            _ => throw new InvalidOperationException("A tool definition is read here, never invoked.")).ProtocolTool;

        return McpFootprint.Of("websearch", published)
            .ShouldNotBeNull($"{typeof(TTool).Name} declares nothing, so every call beside it would wait").For(call);
    }

    private static Dictionary<string, object?> Call(params (string Name, object? Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => a.Value);
}