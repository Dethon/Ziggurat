using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class FileSystemToolFeature(
    IVirtualFileSystemRegistry registry, ReadImageSupport? readImages = null) : IDomainToolFeature
{
    private const string Feature = "filesystem";

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

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        PathStartsAtAMount,
        CapabilitiesAreAdvertised,
        AnEnvelopeIsDataNotAReasonToRetry,
        ExecWorkGoesWhereExecLives,
        TransferIsOneCall
    ];

    public string FeatureName => Feature;

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
            (VfsExecTool.Key, () => AIFunctionFactory.Create(new VfsExecTool(registry).RunAsync, name: $"domain__{Feature}__{VfsExecTool.Name}")),
            (VfsFileInfoTool.Key, () => AIFunctionFactory.Create(new VfsFileInfoTool(registry).RunAsync, name: $"domain__{Feature}__{VfsFileInfoTool.Name}")),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => t.Factory());
    }

    private string? BuildPrompt()
    {
        var mounts = registry.GetMounts();
        if (mounts.Count == 0)
        {
            return null;
        }

        var mountList = string.Join("\n", mounts.Select(FormatMount));
        return $$"""
            ## Available Filesystems

            All `domain__filesystem__*` tool paths must start with one of these mount prefixes. Pick the mount whose description matches your task; don't scatter related files across mounts.
            {{mountList}}

            ### How capabilities work

            Each mount is backed by a different MCP server, and **each backend implements only the operations that make sense for it** — read-only mounts won't accept writes, non-shell mounts won't accept `exec`, and so on. Each mount lists the operations it supports above — call only an operation a mount advertises, so you don't waste a turn discovering an unsupported one by trial and error.

            If you call a tool the backend doesn't implement, the response is a structured error envelope (`{"ok": false, "errorCode": "unsupported_operation", "message": "...", "retryable": false, "hint": "..."}`) — treat it as data, not as an exception. Use it as a hint to pick a different mount or a different operation, not as a reason to retry.

            ### Choosing a mount

            - Programmatic work — parsing, transforming, scraping, extracting archives, generating charts, exercising a CLI — belongs on a mount that advertises `exec`. Hand-editing is fragile for these.
            - A targeted text change belongs on the mount that owns the file: edit it in place rather than scripting the edit somewhere else.
            - When a task spans mounts, run the computation where `exec` lives and persist the readable result on the mount that owns it.

            ### Cross-mount reminders

            - Each mount is its own backend. Tools see only the filesystem of the mount you target — they cannot reach files on a different mount. If you need data from one mount available to a command on another (e.g. for `exec`), copy it across first.
            - `move` and `copy` accept source and destination on different mounts and handle the transfer natively (streaming for cross-FS, recursing into directories) — prefer a single `copy`/`move` call over reading on one mount and creating on another.
            - Paths are virtual: always include the mount prefix. Don't pass bare `/home/...` or `/notes/...` — start with one of the mount points listed above.
            """;
    }

    private static string FormatMount(FileSystemMount mount)
    {
        var line = $"- `{mount.MountPoint}` — {mount.Description}";
        return mount.Capabilities.Count > 0
            ? $"{line}\n  - operations: {string.Join(", ", mount.Capabilities)}"
            : line;
    }
}