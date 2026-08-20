using System.Reflection;
using System.Text.RegularExpressions;
using Domain.Prompts;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Printing.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Timers.Vfs;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// A prompt is the one place in the repo where a rename fails nothing. It teaches the model a tool
// name, a mount root or a worked example, and when the thing it names moves, the sentence stays
// exactly as convincing as it was — the model then calls something that does not exist and the
// turn dies somewhere else entirely. These walk the prompts against the code they describe.
public class PromptStalenessTests
{
    // Mount roots that exist. The first five are the filesystems' own names; the last two are the
    // disk roots their servers construct by name (`McpServerVault`, `McpServerSandbox`), which is
    // a deployment's choice rather than a constant here.
    private static readonly string[] _mountRoots =
    [
        ScheduleFileSystem.Name,
        TimerFileSystem.Name,
        HaFileSystem.Name,
        PrinterQueueFileSystem.Name,
        MediaLibraryDiskFileSystem.Name,
        "vault",
        "sandbox"
    ];

    // Paths that are not mounts and are not meant to be: what a command sees from inside the
    // sandbox container, spelled as the container spells it.
    private static readonly string[] _nativePaths = ["/etc", "/home", "/tmp", "/usr", "/var", "/"];

    public static TheoryData<string> Sections =>
        [.. AgentPromptFixture.ServedText.Keys.Concat(AgentPromptFixture.FeatureText.Keys)];

    // Every prompt any server in this solution serves, found the way the agent finds them: by
    // asking, rather than by a list somebody maintains. A server that adds a prompt gets a budget
    // and a place, or this fails and says which one it was.
    [Fact]
    public void Manifest_EveryPromptAServerServes_IsDeclaredAndAttributedToThatServer()
    {
        var served = ServedPromptNames();

        served.ShouldNotBeEmpty("no MCP server prompts were found; the scan itself is broken");

        foreach (var name in served)
        {
            var declaration = PromptManifest.Find(name);

            declaration.ShouldNotBeNull(
                $"'{name}' is served over MCP but not declared in PromptManifest, so nothing " +
                "budgets it, places it or says what it is for");
            declaration.ServedBy.ShouldNotBeNullOrWhiteSpace(
                $"'{name}' is served by a server, so its declaration must name which one");
        }
    }

    // The other direction: a manifest entry claiming to come from a server that no longer serves it
    // is a budget nobody spends and a section that silently stopped appearing.
    [Fact]
    public void Manifest_EveryServedByDeclaration_MatchesAPromptSomeServerActuallyServes()
    {
        var served = ServedPromptNames();

        PromptManifest.Declarations
            .Where(d => d.ServedBy is not null)
            .Select(d => d.Name)
            .Where(name => !served.Contains(name))
            .ShouldBeEmpty();
    }

    // A server added to an agent's endpoints brings its prompt with it, into every one of that
    // agent's turns. This is where that arrival is noticed.
    [Fact]
    public void Manifest_EveryServerAnAgentDials_HasItsPromptsDeclared()
    {
        var undeclared = AgentPromptFixture.Agents
            .SelectMany(a => a.McpServerEndpoints)
            .Concat(AgentPromptFixture.SubAgents.SelectMany(a => a.McpServerEndpoints))
            .Select(AgentPromptFixture.ServiceOf)
            .Distinct()
            .Where(service => !PromptManifest.Declarations.Any(d => d.ServedBy == service))
            .ToList();

        undeclared.ShouldBeEmpty(
            "these services are dialled by a configured agent but declare no prompt in the " +
            "manifest; declare what they serve, or nothing budgets it");
    }

    // No tool is exposed under an `fs_` prefix — those are the raw MCP tools, filtered out whenever
    // the domain filesystem tools are active — so a prompt naming one teaches a call the model can
    // never make.
    [Theory]
    [MemberData(nameof(Sections))]
    public void Section_NamesNoToolUnderThePrefixNothingIsExposedUnder(string name)
    {
        var phantom = Regex.Matches(TextOf(name), @"\bfs_[a-z_]+")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        phantom.ShouldBeEmpty($"{name} names tools that are not exposed to the model");
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void Section_EveryPathItTeaches_StartsAtAMountThatExists(string name)
    {
        // A whole backticked span, never a prefix of one: `media_content_id`/URI would otherwise
        // read as a path called /URI, which is a closing backtick and a sentence.
        var stale = Regex.Matches(TextOf(name), @"`(/[^`\n]*)`")
            .Select(m => m.Groups[1].Value.TrimStart('/').Split('/')[0])
            // A first segment with a space in it is prose that happened to sit between two
            // backticks, not a mount: `media_content_id`/URI you cannot know. Only `...`.
            .Where(root => !root.Any(char.IsWhiteSpace))
            .Select(root => "/" + root)
            .Distinct()
            .Where(path => !_mountRoots.Contains(path.TrimStart('/'), StringComparer.OrdinalIgnoreCase))
            .Where(path => !_nativePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();

        stale.ShouldBeEmpty(
            $"{name} teaches paths under roots nothing mounts: {string.Join(", ", stale)}");
    }

    // The scheduling prompt is worked examples end to end, so it names more tools than any other.
    [Fact]
    public void SchedulingPrompt_NamesTheToolLeavesThatAreActuallyExposed()
    {
        var prompt = AgentPromptFixture.ServedText[SchedulingPrompt.Name];

        foreach (var tool in (string[])
                 [
                     VfsTextCreateTool.Name, VfsGlobFilesTool.Name, VfsTextEditTool.Name,
                     VfsMoveTool.Name, VfsRemoveTool.Name, VfsExecTool.Name
                 ])
        {
            prompt.ShouldContain($"`{tool}`");
        }

        prompt.ShouldContain("Europe/Madrid");
    }

    private static string TextOf(string name) =>
        AgentPromptFixture.ServedText.TryGetValue(name, out var served)
            ? served
            : AgentPromptFixture.FeatureText[name];

    // Loaded from the test output, where every server this solution builds has been copied. Asking
    // the assemblies rather than reading the source keeps the answer exactly what the SDK will
    // register at runtime.
    private static IReadOnlySet<string> ServedPromptNames() =>
        Directory.GetFiles(AppContext.BaseDirectory, "McpServer*.dll")
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}