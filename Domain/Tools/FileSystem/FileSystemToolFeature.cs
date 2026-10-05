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

    // The falsifiable statements the section above makes. They are declared here rather than in
    // Domain/Prompts because this is where the words are: the mounts section is generated from the
    // registry a session actually has, so its prose has no file of its own.
    public static readonly PromptClaim PathStartsAtAMount =
        new("mounts.path-starts-at-a-mount",
            "Every filesystem tool path starts at one of the mount prefixes the session declares, rather than at a bare path.");

    public static readonly PromptClaim CapabilitiesAreAdvertised =
        new("mounts.capabilities-are-advertised",
            "An operation is called only on a mount that advertises it, rather than discovered to be unsupported by trying.");

    public static readonly PromptClaim AnEnvelopeIsDataNotAReasonToRetry =
        new("mounts.an-envelope-is-data-not-a-reason-to-retry",
            "An error envelope is read as a hint to pick a different mount or operation, and never as a reason to retry the same call.");

    public static readonly PromptClaim ExecWorkGoesWhereExecLives =
        new("mounts.exec-work-goes-where-exec-lives",
            "Programmatic work runs on a mount that advertises exec, and the readable result is persisted on the mount that owns it.");

    public static readonly PromptClaim TransferIsOneCall =
        new("mounts.transfer-is-one-call",
            "Data needed on another mount is moved with a single copy or move call rather than a read on one and a create on the other.");

    public static readonly PromptClaim AnUnmountedPathIsAnswered =
        new("mounts.an-unmounted-path-is-answered",
            "A path under none of the session's mounts is answered with a sentence saying it is not reachable, never hunted for through other tools, searched for across a mount or handed to a worker.");

    public static readonly PromptClaim AnOutpostIsNeverInTheSandbox =
        new("mounts.an-outpost-is-never-in-the-sandbox",
            "A path on a machine (outpost:<NAME>) is never looked for from a sandbox command, which cannot reach it; it is reached only by the file tools on that machine's own address.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        AnOutpostIsNeverInTheSandbox,
        PathStartsAtAMount,
        CapabilitiesAreAdvertised,
        AnEnvelopeIsDataNotAReasonToRetry,
        ExecWorkGoesWhereExecLives,
        TransferIsOneCall,
        AnUnmountedPathIsAnswered
    ];

    public string FeatureName => Feature;

    // Each tool's leaf name against the key the feature config enables it by, read off the one list,
    // so the exec bridge can ask whether this session offers the tool an operation stands for.
    private static readonly IReadOnlyDictionary<string, string> _keysByName = FileSystemOperations.All
        .Where(o => o.ToolKey is not null && o.Capability is not null)
        .ToDictionary(o => o.Capability!, o => o.ToolKey!, StringComparer.Ordinal);

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (VfsFileReadTool.Key, () => AIFunctionFactory.Create(new VfsFileReadTool(registry, readImages).RunAsync, name: $"domain__{Feature}__{VfsFileReadTool.Name}")),
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
                })),
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
                })),
            // These two descriptions interpolate the walk budgets, so they cannot live in a
            // [Description] attribute and are handed to the factory here instead.
            (VfsGlobFilesTool.Key, () => AIFunctionFactory.Create(
                new VfsGlobFilesTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsGlobFilesTool.Name}",
                    Description = VfsGlobFilesTool.ToolDescription
                })),
            (VfsTextSearchTool.Key, () => AIFunctionFactory.Create(
                new VfsTextSearchTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextSearchTool.Name}",
                    Description = VfsTextSearchTool.ToolDescription
                })),
            (VfsMoveTool.Key, () => AIFunctionFactory.Create(new VfsMoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsMoveTool.Name}")),
            (VfsCopyTool.Key, () => AIFunctionFactory.Create(new VfsCopyTool(registry).RunAsync, name: $"domain__{Feature}__{VfsCopyTool.Name}")),
            (VfsRemoveTool.Key, () => AIFunctionFactory.Create(new VfsRemoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsRemoveTool.Name}")),
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
                ExecReach.Over(registry, reroutes: bridge is not null))),
            (VfsFileInfoTool.Key, () => AIFunctionFactory.Create(new VfsFileInfoTool(registry).RunAsync, name: $"domain__{Feature}__{VfsFileInfoTool.Name}")),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => t.Factory());
    }

    // Whether this session's sandbox commands see its other mounts: a sandbox, and a bridge to serve
    // them through.
    private bool ShellReachesMounts => bridge is not null && ShellSection(registry.GetMounts()).Length > 0;

    // What a sandbox command reaches of the session's other mounts, and where. Static so the prompt
    // snapshots can build it from sample mounts exactly as a session builds it from live ones.
    public static string ShellSection(IReadOnlyList<FileSystemMount> mounts)
    {
        var sandbox = mounts.FirstOrDefault(m => m.ShellReach == ShellReach.Contained);
        var served = mounts.Where(VfsCall.IsServed).ToList();
        if (sandbox is null || served.Count == 0)
        {
            return "";
        }

        var occupied = sandbox.OccupiedNames ?? [];
        var places = string.Join(", ", served.Select(m => m.MountPoint.Trim('/') is var name && occupied.Contains(name)
            ? $"`{m.MountPoint}` only at `/vfs/{name}` (the sandbox has a `{m.MountPoint}` of its own)"
            : $"`{m.MountPoint}`"));
        // Every served mount is rerouted, but only the ones that offer `exec` are named: those are
        // the mounts the model is told it can exec on, so they are the ones whose exec moved.
        var offeringExec = served.Where(m => m.Capabilities.Contains(VfsExecTool.Name)).Select(m => $"`{m.MountPoint}`").ToList();
        var rerouted = offeringExec.Count == 0
            ? ""
            : $" `exec` on {string.Join(", ", offeringExec)} runs in the sandbox, with that directory as the working directory.";

        return $$"""
            ### The other mounts inside a command

            A command run with `exec` on `{{sandbox.MountPoint}}` sees every mount above but the machines as an ordinary directory, at the same path the tools take: {{places}}. Pipes, `grep -r`, `jq`, `sed -i` and scripts in any language work on them, and an action file runs as `./<name>` from its directory, or by its path, from any script.{{rerouted}} A machine is never inside a command: reach one only with the file tools, at its own `outpost:` address.

            A command can do there only what the file tools would do unasked — a write is a `text_create` (or a copy, for anything that is not text), `rm` a `remove`, `mv` a `move` — and what would need the person's approval is refused. Bash does not report a refused write, so read the result's `vfsChanges`: every change the command made through these mounts, `applied`, `refused` with the mount's own reason, or `dropped` because a timeout cut it off. `vfsTruncated` names a directory a recursive command saw only part of.
            """;
    }

    private string? BuildPrompt()
    {
        var mounts = registry.GetMounts();
        if (mounts.Count == 0)
        {
            return null;
        }

        var mountList = string.Join("\n", mounts.Where(m => !IsMachine(m)).Select(FormatMount));
        var machines = MachinesSection(mounts);
        var shell = ShellReachesMounts ? ShellSection(mounts) : "";
        return $$"""
            ## Available Filesystems

            All `domain__filesystem__*` tool paths must start with one of these mount prefixes. Pick the mount whose description matches your task; don't scatter related files across mounts.
            {{mountList}}
            {{(machines.Length > 0 ? $"\n{machines}\n" : "")}}{{(shell.Length > 0 ? $"\n{shell}\n" : "")}}
            ### How capabilities work

            Each mount is backed by a different MCP server, and **each backend implements only the operations that make sense for it** — read-only mounts won't accept writes, non-shell mounts won't accept `exec`, and so on. Each mount lists the operations it supports above — call only an operation a mount advertises, so you don't waste a turn discovering an unsupported one by trial and error.

            If you call a tool the backend doesn't implement, the response is a structured error envelope (`{"ok": false, "errorCode": "unsupported_operation", "message": "...", "retryable": false, "hint": "..."}`) — treat it as data, not as an exception. Use it as a hint to pick a different mount or a different operation, not as a reason to retry.

            ### Choosing a mount

            - Programmatic work — parsing, transforming, scraping, extracting archives, generating charts, exercising a CLI — belongs on a mount that advertises `exec`. Hand-editing is fragile for these.
            - A targeted text change belongs on the mount that owns the file: edit it in place rather than scripting the edit somewhere else.
            - When a task spans mounts, run the computation where `exec` lives and persist the readable result on the mount that owns it.

            ### Cross-mount reminders

            - Each mount is its own backend. Tools see only the filesystem of the mount you target — they cannot reach files on a different mount. {{(shell.Length > 0 ? "A sandbox command sees the other mounts directly (above), so data a command needs from one stays where it is." : "If you need data from one mount available to a command on another (e.g. for `exec`), copy it across first.")}}
            - `move` and `copy` accept source and destination on different mounts and handle the transfer natively (streaming for cross-FS, recursing into directories) — prefer a single `copy`/`move` call over reading on one mount and creating on another.
            - Paths are virtual: always include the mount prefix. Don't pass bare `/home/...` or `/notes/...` — start with one of the mount points listed above.
            - A path that starts under none of these mounts is not reachable in this session, by any tool or by a worker — the mount list above is complete. Say so in one sentence instead of hunting for it: no retries under other spellings, no search of a mount for a folder of that name (a `find` or a glob from the mount's root, a look through its home directory), no web tools, no delegation.
            """;
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
            : $"""
              ### Other machines

              These are not part of your filesystem. Each is a separate computer, somebody's own, that offered its files to this conversation and can be gone in the next one. Address one as `{OutpostMountPoint.Scheme}<NAME>/<absolute path on that machine>` — never as `/<NAME>`, which would be a path in your own tree. A copy or move to or from one sends data between computers, and its `exec` runs on that person's machine, not in a sandbox.
              {string.Join("\n", machines.Select(FormatMount))}
              """;
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