using System.Reflection;
using System.Text.RegularExpressions;
using Domain.Prompts;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Printing.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

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
        [.. AgentPromptFixture.ServedText.Keys.Concat(AgentPromptFixture.FeatureText.Keys)
            .Concat(AgentPromptFixture.ServedSkills.Keys)];

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

    // Skills walk the same two directions as prompts. Found the way the agent finds them: off each
    // server's real registration, as the resources it would publish.
    [Fact]
    public void Manifest_EverySkillAServerServes_IsDeclaredAndAttributedToThatServer()
    {
        var served = ServedSkills();

        served.ShouldNotBeEmpty("no MCP server skills were found; the scan itself is broken");

        foreach (var (name, service) in served)
        {
            var declaration = PromptManifest.FindSkill(name);

            declaration.ShouldNotBeNull(
                $"'{name}' is served by {service} but not declared in PromptManifest.Skills, so nothing " +
                "budgets it or says what it claims");
            declaration.ServedBy.ShouldBe(service, $"'{name}' is declared as served by another server");
        }
    }

    [Fact]
    public void Manifest_EverySkillDeclaration_MatchesASkillSomeServerActuallyServes()
    {
        var served = ServedSkills();

        PromptManifest.Skills
            .Where(s => !served.Contains((s.Name, s.ServedBy)))
            .Select(s => $"{s.Name} from {s.ServedBy}")
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

    // A filesystem tool named as a call the model should make is named as the model can call it:
    // `domain__filesystem__file_read`, never the bare `file_read`. The bare form is the tool's own
    // name inside the feature, and a model that reads the prompt literally calls it and gets
    // nothing back. gpt-5.6-luna mapped the bare name onto the real tool and hid this for as long
    // as it was the only model the eval ran; glm-5.3-flash calls what the prompt actually says.
    // Prose *about* a tool ("don't `file_read` them") is not a call, so only a name followed by an
    // argument — a path, or a parenthesised call — has to carry the prefix.
    [Theory]
    [MemberData(nameof(Sections))]
    public void Section_NamesAFilesystemCall_WithThePrefixTheModelCanCall(string name)
    {
        var bare = Regex.Matches(
                TextOf(name),
                @"`(?<tool>file_read|file_write|text_search|text_create|text_edit|glob|exec|move|copy|remove|file_info)`?\s*[(`]?\s*(?<arg>/[A-Za-z0-9_./<>*-]+|path=|command=|[a-z_.]+\.sh)")
            .Select(m => $"{m.Groups["tool"].Value} {m.Groups["arg"].Value}")
            .Distinct()
            .ToList();

        bare.ShouldBeEmpty(
            $"{name} tells the model to call a filesystem tool by its bare name; the model can " +
            "only call it as domain__filesystem__<name>");
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

    // The scheduling skill is worked examples end to end, so it names more tools than any other.
    [Fact]
    public void SchedulingSkill_NamesTheToolLeavesThatAreActuallyExposed()
    {
        var prompt = AgentPromptFixture.ServedSkills[SchedulingSkill.Name].Body;

        foreach (var tool in (string[])
                 [
                     VfsTextCreateTool.Name, VfsGlobFilesTool.Name, VfsTextEditTool.Name,
                     VfsMoveTool.Name, VfsRemoveTool.Name, VfsExecTool.Name
                 ])
        {
            // Named as the model can call it: the bare leaf is the tool's name inside the feature,
            // not the name the agent exposes.
            prompt.ShouldContain($"domain__filesystem__{tool}");
        }

        prompt.ShouldContain("Europe/Madrid");
    }

    // The core directive tells the model never to hedge and never to add an unsolicited warning,
    // and it is the first section every agent reads. The one thing that must survive it is the
    // question before an irreversible change: a bulk delete of the user's own notes is not a
    // refusal, and a model that reads "your role is to assist, not to gatekeep" as covering it
    // deletes seven notes and reports it done. glm-5.3-flash did exactly that, three runs of
    // three; gpt-5.6-luna resolved the contradiction by judgement and hid it.
    [Fact]
    public void CoreDirective_CarvesOutTheQuestionBeforeAnIrreversibleChange()
    {
        var text = CoreDirectivePrompt.Instructions;

        text.ShouldContain("irreversible",
            Case.Insensitive,
            "the section that governs refusals has to say that asking before destroying " +
            "something unrecoverable is not the hedging it forbids");

        // The first wording said only "ask one short question first", and gpt-5.6-luna read that
        // as stop-and-wait: it asked without loading the vault skill or looking at the folder, and
        // the scenario that wants the question went from 1.00 to 0.00 for the opposite reason it
        // had failed on glm. The question is asked from the work, not instead of it.
        text.ShouldContain("what it would",
            Case.Insensitive,
            "and has to say the looking still happens — the question names what would be lost, " +
            "so the turn does the reading that finds out");
    }

    // The scope rule's three examples all run one way — asked to switch a device on, do not also
    // choose its settings — and a model that reads them as a pattern rather than a principle
    // completes the other direction happily: told "pon el aire del salón a veintidós grados",
    // glm-5.3-flash ran `turn_on.sh && set_temperature.sh` and moved a device the turn never
    // mentioned. The converse has to be an example too, not an inference from these.
    [Fact]
    public void HomeScope_SaysThatSettingAValue_DoesNotAlsoSwitchTheDeviceOn()
    {
        var scope = HomeAssistantPrompt.SystemPrompt;

        scope.ShouldContain("turn_on.sh",
            Case.Insensitive,
            "the scope rule has to name the action a value-setting request must not add");
        scope.ShouldContain("already",
            Case.Insensitive,
            "and say that a device's current state is not the turn's business to change");
    }

    // A watch's entity_id is the bare id, and every other rule about entities says to use the
    // directory name verbatim — so "the id from the setup index" resolved, for glm-5.3-flash, to
    // the directory the index lists: sensor.temperatura_salon_(temperatura-salon). The watch was
    // otherwise perfect. The two spellings have to be told apart where the field is described.
    [Fact]
    public void WatchesSkill_SaysTheEntityIdIsTheBareId_NotTheDirectoryName()
    {
        var body = AgentPromptFixture.ServedSkills[HomeWatchesSkill.Name].Body;

        body.ShouldContain("without the",
            Case.Insensitive,
            "the watch file's entity_id is the bare id, so the skill has to say that the " +
            "_(friendly-name) suffix a directory carries is not part of it");
    }

    // The bare-name guard reads assembled text, so an interpolated `{{VfsExecTool.Name}}` slipped
    // past it: the source spells no tool name at all, and the text it produces spells "exec". The
    // whole countdown-timers skill taught five uncallable tools that way. Interpolate through
    // FileSystemToolFeature.Callable instead, which builds the prefix the feature registers under.
    [Fact]
    public void NoPromptSource_InterpolatesABareToolName()
    {
        var offenders = Directory
            .EnumerateFiles(PromptSourceDirectory, "*.cs")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\{\{Vfs\w+Tool\.Name\}\}"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty(
            "these prompts interpolate a tool's bare leaf name; wrap it in " +
            "FileSystemToolFeature.Callable so the model is told the name it can call");
    }

    private static readonly string PromptSourceDirectory = Path.Combine(
        RepositoryRoot(), "Domain", "Prompts");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Domain", "Prompts")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // "unless they explicitly ask what you remember" is a wide door: asked "¿y por qué nueve
    // minutos?" — why the timer is what it is, not what is stored — glm-5.3-flash read itself as
    // invited and answered "lo dejé guardado como tu preferencia". A why-question about a
    // remembered preference is answered with the preference, never with the remembering.
    [Fact]
    public void MemoryPrompt_SaysAWhyQuestion_IsNotAnInvitationToNameTheStore()
    {
        var text = MemoryPrompts.FeatureSystemPrompt;

        text.ShouldContain("why",
            Case.Insensitive,
            "the memory rule has to say which side of its exception a why-question falls on");
    }

    // The vault prompt ends by inviting a free search of the notes, and nothing said when the
    // notes are the wrong place to look. Asked "busca cuánto tiene que reposar el gazpacho de
    // Almudena" — a recipe from a neighbourhood website, named nowhere in the vault —
    // glm-5.3-flash searched /vault first, every run. A question about the world is a question
    // for the web.
    [Fact]
    public void VaultPrompt_SaysWhichSearchesAreNotTheVaults()
    {
        VaultPrompt.Prompt.ShouldContain("the web",
            Case.Insensitive,
            "the section that invites searching the vault has to say what the vault is not for, " +
            "or a question about the world is answered by grepping the user's notes");
    }

    private static string TextOf(string name) =>
        AgentPromptFixture.ServedText.TryGetValue(name, out var served)
            ? served
            : AgentPromptFixture.FeatureText.TryGetValue(name, out var feature)
                ? feature
                : AgentPromptFixture.ServedSkills[name].Body;

    // Every skill body resource each server's real registration publishes, named with the service
    // the deployment dials that server as. The compose service name is the row's id under the
    // `mcp-` prefix every tool server shares.
    private static IReadOnlySet<(string Name, string Service)> ServedSkills() =>
        McpServerRegistrations.All
            .SelectMany(row =>
            {
                var services = new ServiceCollection();
                row.Configure(services);
                using var provider = services.BuildServiceProvider();
                return provider.GetServices<McpServerResource>()
                    .Select(resource => resource.ProtocolResourceTemplate.UriTemplate)
                    .Where(uri => uri != SkillServerResources.IndexAddress
                                  && uri.StartsWith("skills://", StringComparison.Ordinal))
                    .Select(uri => (Name: uri["skills://".Length..].Split('/')[0], Service: "mcp-" + row.Id))
                    .ToList();
            })
            .ToHashSet();

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