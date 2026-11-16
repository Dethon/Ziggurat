using Domain.Contracts;
using Domain.DTOs;
using Domain.Outposts;
using Domain.Prompts;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class FileSystemToolFeature(
    IVirtualFileSystemRegistry registry, ReadImageSupport? readImages = null, IVfsBridge? bridge = null)
    : IDomainToolFeature
{
    private const string Feature = "filesystem";

    // The name the model can actually call, built once here so a prompt that teaches a call can
    // interpolate it instead of spelling the prefix — or, as the timers skill did, interpolating
    // the bare leaf and teaching a tool that does not exist.
    public static string Callable(string toolName) => $"domain__{Feature}__{toolName}";

    // The keys the feature config can enable, derived from the operations that have a domain tool
    // — so a new operation appears here as soon as it is added to the one list.
    public static readonly IReadOnlySet<string> AllToolKeys = FileSystemOperations.All
        .Where(o => o.ToolKey is not null)
        .Select(o => o.ToolKey!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The mounts section's words and claims are its document's (Sections/filesystem_mounts.md); what
    // is generated here is only what the registry a session actually has decides — which mounts,
    // which machines, which parts of the words apply.
    private static readonly PromptDocument _document = PromptDocuments.Section(PromptManifest.FilesystemMounts);

    private static readonly PromptTemplate _section = _document.Template("mounts", "machines", "shell", "reach");

    private static readonly PromptTemplate _machines = _document.Part("machines", "scheme", "list");

    private static readonly PromptTemplate _shell = _document.Part("shell", "sandbox", "places", "rerouted");

    private static readonly PromptTemplate _rerouted = _document.Part("rerouted", "exec_mounts");

    private static readonly PromptTemplate _occupiedPlace = _document.Part("occupied-place", "mount", "path");

    private static readonly string _reachInACommand = _document.Part("reach-in-a-command").Fill();

    private static readonly string _reachByCopy = _document.Part("reach-by-copy").Fill();

    public static readonly PromptClaim PathStartsAtAMount =
        _document.Claim("mounts.path-starts-at-a-mount");

    public static readonly PromptClaim CapabilitiesAreAdvertised =
        _document.Claim("mounts.capabilities-are-advertised");

    public static readonly PromptClaim AnEnvelopeIsDataNotAReasonToRetry =
        _document.Claim("mounts.an-envelope-is-data-not-a-reason-to-retry");

    public static readonly PromptClaim ExecWorkGoesWhereExecLives =
        _document.Claim("mounts.exec-work-goes-where-exec-lives");

    public static readonly PromptClaim TransferIsOneCall =
        _document.Claim("mounts.transfer-is-one-call");

    public static readonly PromptClaim AnUnmountedPathIsAnswered =
        _document.Claim("mounts.an-unmounted-path-is-answered");

    public static readonly PromptClaim AnOutpostIsNeverInTheSandbox =
        _document.Claim("mounts.an-outpost-is-never-in-the-sandbox");

    public string FeatureName => Feature;

    // Each tool's leaf name against the key the feature config enables it by, read off the one list,
    // so the exec bridge can ask whether this session offers the tool an operation stands for.
    private static readonly IReadOnlyDictionary<string, string> _keysByName = FileSystemOperations.All
        .Where(o => o.ToolKey is not null && o.Capability is not null)
        .ToDictionary(o => o.Capability!, o => o.ToolKey!, StringComparer.Ordinal);

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        // Each tool beside what a call to it touches, read off the call's own path arguments by
        // the names the tool's method gives them: what lets two calls of one message run at once
        // or makes the later wait for the earlier (CallFootprint).
        var touched = new FileSystemFootprint(registry);
        var reach = ExecReach.Over(registry, reroutes: bridge is not null);
        var tools = new (string Key, Func<AIFunction> Factory, Func<IReadOnlyDictionary<string, object?>, CallFootprint> Footprint)[]
        {
            (VfsFileReadTool.Key, () => AIFunctionFactory.Create(new VfsFileReadTool(registry, readImages).RunAsync, name: $"domain__{Feature}__{VfsFileReadTool.Name}"),
                call => touched.Reading(call, "filePath")),
            (VfsTextCreateTool.Key, () => AIFunctionFactory.Create(
                new VfsTextCreateTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextCreateTool.Name}",
                    ConfigureParameterBinding = parameter => parameter.Name == "content"
                        ? new AIFunctionFactoryOptions.ParameterBindingOptions
                        {
                            BindParameter = (_, args) =>
                                TextArg.Coerce(args.TryGetValue("content", out var raw) ? raw : null)
                        }
                        : default
                }),
                call => touched.Changing(call, "filePath")),
            (VfsTextEditTool.Key, () => AIFunctionFactory.Create(
                new VfsTextEditTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextEditTool.Name}",
                    ConfigureParameterBinding = parameter => parameter.Name == "edits"
                        ? new AIFunctionFactoryOptions.ParameterBindingOptions
                        {
                            BindParameter = (_, args) =>
                                TextArg.CoerceEdits(args.TryGetValue("edits", out var raw) ? raw : null)
                        }
                        : default
                }),
                call => touched.Changing(call, "filePath")),
            // These two descriptions interpolate the walk budgets, so they cannot live in a
            // [Description] attribute and are handed to the factory here instead.
            (VfsGlobFilesTool.Key, () => AIFunctionFactory.Create(
                new VfsGlobFilesTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsGlobFilesTool.Name}",
                    Description = VfsGlobFilesTool.ToolDescription
                }),
                call => touched.Reading(call, "basePath")),
            (VfsTextSearchTool.Key, () => AIFunctionFactory.Create(
                new VfsTextSearchTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextSearchTool.Name}",
                    Description = VfsTextSearchTool.ToolDescription
                }),
                // One file where the call names one, as the tool itself prefers it.
                call => touched.Reading(call, CallArguments.Text(call, "filePath") is null ? "directoryPath" : "filePath")),
            (VfsMoveTool.Key, () => AIFunctionFactory.Create(new VfsMoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsMoveTool.Name}"),
                call => touched.Changing(call, "sourcePath", "destinationPath")),
            (VfsCopyTool.Key, () => AIFunctionFactory.Create(new VfsCopyTool(registry).RunAsync, name: $"domain__{Feature}__{VfsCopyTool.Name}"),
                call => touched.Reading(call, "sourcePath").And(touched.Changing(call, "destinationPath"))),
            (VfsRemoveTool.Key, () => AIFunctionFactory.Create(new VfsRemoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsRemoveTool.Name}"),
                call => touched.Changing(call, "path")),
            // Carries where each call would run, read off this session's mounts, for the exec screen.
            (VfsExecTool.Key, () => ExecReach.Carried(
                AIFunctionFactory.Create(
                    new VfsExecTool(registry, bridge, name =>
                        config.EnabledTools is null
                        || (_keysByName.TryGetValue(name, out var key) && config.EnabledTools.Contains(key))).RunAsync,
                    new AIFunctionFactoryOptions
                    {
                        Name = $"domain__{Feature}__{VfsExecTool.Name}",
                        Description = ShellReachesMounts
                            ? $"{VfsExecTool.ToolDescription}\n{VfsExecTool.BridgedDescription}"
                            : VfsExecTool.ToolDescription
                    }),
                reach),
                call => touched.Executing(reach, call)),
            (VfsFileInfoTool.Key, () => AIFunctionFactory.Create(new VfsFileInfoTool(registry).RunAsync, name: $"domain__{Feature}__{VfsFileInfoTool.Name}"),
                call => touched.Reading(call, "path")),
            (VfsQueryTool.Key, () => AIFunctionFactory.Create(new VfsQueryTool(registry).RunAsync, name: $"domain__{Feature}__{VfsQueryTool.Name}"),
                call => touched.Reading(call, "path")),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => FootprintDeclaration.Declared(t.Factory(), t.Footprint));
    }

    // Whether this session's sandbox commands see its other mounts: a sandbox, and a bridge to serve
    // them through.
    private bool ShellReachesMounts => bridge is not null && ShellSection(registry.GetMounts()).Length > 0;

    // What a sandbox command reaches of the session's other mounts, and where. Static so the prompt
    // snapshots can build it from sample mounts exactly as a session builds it from live ones.
    public static string ShellSection(IReadOnlyList<FileSystemMount> mounts)
    {
        var sandbox = ExecReach.Sandbox(mounts);
        var served = mounts.Where(VfsCall.IsServed).ToList();
        if (sandbox is null || served.Count == 0)
        {
            return "";
        }

        var occupied = sandbox.OccupiedNames ?? [];
        var places = string.Join(", ", served.Select(m => occupied.Contains(ServedMount.NameOf(m.MountPoint))
            ? _occupiedPlace.Fill(("mount", m.MountPoint), ("path", ServedMount.PathOf(m.MountPoint)))
            : $"`{m.MountPoint}`"));
        // Every served mount is rerouted, but only the ones that offer `exec` are named: those are
        // the mounts the model is told it can exec on, so they are the ones whose exec moved.
        var offeringExec = served.Where(m => m.Capabilities.Contains(VfsExecTool.Name)).Select(m => $"`{m.MountPoint}`").ToList();
        var rerouted = offeringExec.Count == 0
            ? ""
            : " " + _rerouted.Fill(("exec_mounts", string.Join(", ", offeringExec)));

        return _shell.Fill(("sandbox", sandbox.MountPoint), ("places", places), ("rerouted", rerouted));
    }

    private string? BuildPrompt()
    {
        var mounts = registry.GetMounts();
        if (mounts.Count == 0)
        {
            return null;
        }

        var machines = MachinesSection(mounts);
        var shell = ShellReachesMounts ? ShellSection(mounts) : "";
        return _section.Fill(
            ("mounts", string.Join("\n", mounts.Where(m => !IsMachine(m)).Select(FormatMount))),
            ("machines", machines.Length > 0 ? $"\n{machines}\n" : ""),
            ("shell", shell.Length > 0 ? $"\n{shell}\n" : ""),
            ("reach", shell.Length > 0 ? _reachInACommand : _reachByCopy));
    }

    // The outposts among a session's mounts, under a heading that says what they are, or nothing
    // when there are none. Listed beside the vault, a machine read as one more branch of the tree,
    // and a person's own computer is the last place a guess about where a file lives should land.
    // Public so the prompt snapshots show these words rather than a fixture's paraphrase of them.
    public static string MachinesSection(IEnumerable<FileSystemMount> mounts)
    {
        var machines = mounts.Where(IsMachine).ToList();
        return machines.Count == 0
            ? ""
            : _machines.Fill(
                ("scheme", OutpostMountPoint.Scheme),
                ("list", string.Join("\n", machines.Select(FormatMount))));
    }

    private static bool IsMachine(FileSystemMount mount) => OutpostMountPoint.Addresses(mount.MountPoint);

    private static string FormatMount(FileSystemMount mount)
    {
        var line = $"- `{mount.MountPoint}` — {mount.Description}";
        return mount.Capabilities.Count > 0
            // Spelled as the model calls them: listed bare, the line taught a tool named
            // `file_read`, and a model called exactly that before the not-found reply sent it
            // to the real name.
            ? $"{line}\n  - operations: {string.Join(", ", mount.Capabilities.Select(Callable))}"
            : line;
    }
}