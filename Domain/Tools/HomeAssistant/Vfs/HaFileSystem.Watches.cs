using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

// The one writable subtree of the mount. A watch is a Home Assistant automation; the file is a view
// of it, rendered on write and projected back on read, stored nowhere but in the home. Every other
// path on the mount refuses a write here, by name, so the model learns where writing is possible
// instead of hunting for another spelling.
public sealed partial class HaFileSystem
{
    private static bool IsWatchNode(HaVfsNode node) =>
        node.Kind is HaVfsKind.WatchesRoot or HaVfsKind.WatchDir or HaVfsKind.WatchFile or HaVfsKind.WatchStatusFile;

    private async Task<(bool Exists, bool IsDir)> ResolveWatchAsync(HaVfsNode node, CancellationToken ct)
    {
        if (node.Kind == HaVfsKind.WatchesRoot)
        {
            return (true, true);
        }

        var exists = HaWatchAutomation.IsValidWatchId(node.WatchId) && await _watches.GetAsync(node.WatchId!, ct) is not null;
        return (exists, node.Kind == HaVfsKind.WatchDir);
    }

    private async Task<FsResult<FsReadResult>> ReadWatchAsync(string path, HaVfsNode node, int? offset, int? limit, CancellationToken ct)
    {
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId) || await _watches.GetAsync(node.WatchId!, ct) is not { } watch)
        {
            return NotFound(path);
        }

        var text = node.Kind == HaVfsKind.WatchFile ? watch.Spec.ToJson() : HaWatches.RenderStatus(watch);
        return BuildReadResult(path, text, offset, limit);
    }

    public override async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.WatchFile)
        {
            return RefuseWrite<FsCreateResult>(node, path, "created");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId))
        {
            return InvalidWatchId<FsCreateResult>(node.WatchId);
        }

        var existing = await _watches.GetAsync(node.WatchId!, ct);
        if (existing is not null && !overwrite)
        {
            return FsError.Fail<FsCreateResult>(
                ToolError.Codes.AlreadyExists,
                $"Watch '{node.WatchId}' already exists.",
                "Pass overwrite=true to replace it in place (same id, never a second watch), or fs_edit its watch.json.");
        }

        return await WriteWatchAsync(node.WatchId!, content, existing, ct, () => new FsCreateResult
        {
            Status = existing is null ? "created" : "replaced",
            FilePath = path,
            Size = content.Length.ToString(),
            Lines = content.Split('\n').Length
        });
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.WatchFile)
        {
            return RefuseWrite<FsEditResult>(node, path, "edited");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId) || await _watches.GetAsync(node.WatchId!, ct) is not { } existing)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!TextEdits.Apply(existing.Spec.ToJson(), edits).TryGetValue(out var applied, out var unmatched))
        {
            return new FsResult<FsEditResult>.Err(unmatched with
            {
                Hint = "Read the current watch.json and use its exact text; the file is rendered from the automation, so the spelling is the mount's."
            });
        }

        return await WriteWatchAsync(node.WatchId!, applied.Text, existing, ct, () => new FsEditResult
        {
            Status = "edited",
            FilePath = path,
            TotalOccurrencesReplaced = applied.Total,
            Edits = applied.Details
        });
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind is not (HaVfsKind.WatchDir or HaVfsKind.WatchFile))
        {
            return RefuseWrite<FsRemoveResult>(node, path, "deleted");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId) || await _watches.GetAsync(node.WatchId!, ct) is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        try
        {
            await _watches.DeleteAsync(node.WatchId!, ct);
        }
        catch (HomeAssistantException ex)
        {
            return HomeAssistantFailure<FsRemoveResult>(ex);
        }

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "the automation was removed from the home", OriginalPath = path, TrashPath = ""
        });
    }

    // Parse, validate, render and hand to the home. The home's own refusal — a trigger it does not
    // know, a key it requires — comes back in its own words, so the agent can fix the file in the
    // same turn instead of creating a watch that never fires.
    private async Task<FsResult<T>> WriteWatchAsync<T>(string watchId, string content, HaWatch? existing, CancellationToken ct, Func<T> ok)
        where T : class
    {
        HaWatchSpec spec;
        try
        {
            spec = HaWatchSpec.Parse(content);
        }
        catch (HaWatchSpecException ex)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument, $"Invalid watch.json: {ex.Message}",
                "Fix the field named and write the file again; the guide lists the shape.");
        }

        var caller = _caller();
        var agentId = existing?.Meta.AgentId ?? caller?.AgentId;
        if (agentId is null)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument,
                "This call carries no conversation context, so the watch has no creating agent to run its prompts.",
                "Call from an agent turn; a watch is created by the agent that will answer its fires.");
        }

        // "Warn me" is answered where it was asked. The model is never told which channel a turn
        // came from — only voice decorates the turn with its room — so a file that names no
        // delivery takes the caller's own origin: the speaking satellite on voice, Telegram on
        // Telegram, the chat on the chat. Naming one is how the user sends the answer elsewhere.
        if (spec.DeliverTo is null && caller?.Origin is { } origin)
        {
            spec = spec with { DeliverTo = [origin.Address is null ? origin.ChannelId : $"{origin.ChannelId}:{origin.Address}"] };
        }

        try
        {
            await _watches.WriteAsync(watchId, spec, agentId, existing, ct);
        }
        catch (HomeAssistantConfigRejectedException ex)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument, $"Home Assistant rejected the watch: {ex.Message}",
                "The triggers, conditions and actions are Home Assistant's own JSON; fix the key it names and write the file again.");
        }
        catch (HomeAssistantException ex)
        {
            return HomeAssistantFailure<T>(ex);
        }

        return new FsResult<T>.Ok(ok());
    }

    private static FsResult<T> HomeAssistantFailure<T>(HomeAssistantException ex) where T : class =>
        ex is HomeAssistantNotFoundException
            ? FsError.Fail<T>(ToolError.Codes.NotFound, ex.Message,
                "If every watch write answers 404, this home lacks the `config` integration (part of default_config), so watches are unavailable here.")
            : FsError.Fail<T>(ToolError.CodeFor(ex), ex.Message);

    private static FsResult<T> InvalidWatchId<T>(string? id) where T : class =>
        FsError.Invalid<T>(
            $"'{id}' is not a valid watch id: use a descriptive slug of letters, digits, '-' and '_' (e.g. laura-sugar-high).");

    // A write anywhere but a watch file is refused by name — an existing read-only file as read-only,
    // anything else as not the place — and the refusal says where writing is possible.
    private static FsResult<T> RefuseWrite<T>(HaVfsNode node, string path, string verb) where T : class =>
        node.Kind is HaVfsKind.WatchStatusFile
            ? FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} is read-only: status.json is rendered from the automation; the watch is changed through /ha/watches/<id>/watch.json.",
                "Write or edit the watch.json beside it instead.")
            : FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} cannot be {verb}: /ha is read-only except for /ha/watches/<id>/watch.json.",
                "To act on a device use exec on its action files; to react to the home write a watch.");
}