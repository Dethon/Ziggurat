using Domain.Contracts;
using Domain.DTOs.Channel;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The fetch a channel answers for the files it holds, answered by the stack from the files the
// scenario names. An eval dials no channel (EvalStack), so without this the agent's own
// ChannelAttachmentSource would find nobody to ask and every attachment would reach the model as a
// placeholder. Everything after the fetch — hydration, the capability check, the wire — is the
// deployment's.
public sealed class EvalAttachments(EvalTurn turn) : IAttachmentSource
{
    private readonly Dictionary<string, string> _paths = References(turn)
        .Zip(turn.Attachments, (reference, attachment) => (reference.Id, attachment.Path))
        .ToDictionary(pair => pair.Id, pair => pair.Path, StringComparer.Ordinal);

    private readonly string _channel = Channel(turn);
    private readonly HashSet<string> _served = [];
    private readonly Lock _gate = new();

    // Which attachments reached the model at least once. A benchmark meal whose photo was never
    // served was estimated from words alone, and the scorecard has to be able to say so.
    public IReadOnlyList<string> Served
    {
        get
        {
            lock (_gate)
            {
                return [.. _served.Order(StringComparer.Ordinal)];
            }
        }
    }

    public async Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
    {
        if (channelId != _channel || !_paths.TryGetValue(attachmentId, out var path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        lock (_gate)
        {
            _served.Add(attachmentId);
        }

        return bytes;
    }

    // The references the turn carries, built here so the turn and the fetch agree on every id. A
    // clip's stills are named by the ids of the attachments at those paths.
    public static IReadOnlyList<AttachmentReference> References(EvalTurn turn)
    {
        var ids = turn.Attachments
            .Select((attachment, index) => (attachment.Path, Id: $"eval-attachment-{index + 1}"))
            .DistinctBy(pair => pair.Path, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Path, pair => pair.Id, StringComparer.Ordinal);
        return
        [
            .. turn.Attachments.Select((attachment, index) => new AttachmentReference
            {
                Id = $"eval-attachment-{index + 1}",
                FileName = Path.GetFileName(attachment.Path),
                MediaType = attachment.MediaType,
                SizeBytes = File.Exists(attachment.Path) ? new FileInfo(attachment.Path).Length : 0,
                Stills = attachment.Stills is { } stills ? [.. stills.Where(ids.ContainsKey).Select(still => ids[still])] : null
            })
        ];
    }

    // The channel that holds the files is the one the turn came in on.
    public static string Channel(EvalTurn turn) =>
        turn.Channel ?? (turn.SatelliteId is null ? "signalr" : "voice");
}