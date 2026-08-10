using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Composer;

// Picked with the platform's own control, pasted, or dropped — all three arrive here.
public record AttachFiles(string? TopicId, IReadOnlyList<PickedFile> Files) : IAction;

public record AttachmentLimitsLoaded(AttachmentLimits Limits) : IAction;

public record AttachmentPicked(string TopicId, ComposerAttachment Attachment) : IAction;

public record AttachmentProgressed(string TopicId, string LocalId, int PercentComplete) : IAction;

public record AttachmentUploaded(string TopicId, string LocalId, AttachmentReference Reference) : IAction;

public record AttachmentFailed(string TopicId, string LocalId, string Reason) : IAction;

// Removing a file that finished and cancelling one still uploading are the same thing to the
// state — the file leaves the composer — and different things to the effect, which has an upload
// to abort. One action, so a person who does not know which case they are in still gets both.
public record RemoveAttachment(string TopicId, string LocalId) : IAction;

// Named rather than wholesale: a file picked while the send was in flight has not been sent, and
// clearing the topic's whole list would throw it away with no trace.
public record ClearAttachments(string TopicId, IReadOnlyList<string> LocalIds) : IAction;

public sealed class ComposerStore : IDisposable
{
    private readonly Store<ComposerState> _store;

    public ComposerStore(Dispatcher dispatcher)
    {
        _store = new Store<ComposerState>(ComposerState.Initial);
        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public ComposerState State => _store.State;

    public IObservable<ComposerState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static ComposerState Reduce(ComposerState state, IAction action) => action switch
    {
        AttachmentLimitsLoaded a => state with { Limits = a.Limits },

        AttachmentPicked a => state with
        {
            AttachmentsByTopic = state.AttachmentsByTopic.With(
                a.TopicId, (IReadOnlyList<ComposerAttachment>)[.. state.For(a.TopicId), a.Attachment])
        },

        AttachmentProgressed a => Map(state, a.TopicId, a.LocalId,
            attachment => attachment with { PercentComplete = a.PercentComplete }),

        AttachmentUploaded a => Map(state, a.TopicId, a.LocalId,
            attachment => attachment with
            {
                Status = AttachmentStatus.Ready,
                PercentComplete = 100,
                Reference = a.Reference
            }),

        AttachmentFailed a => Map(state, a.TopicId, a.LocalId,
            attachment => attachment with { Status = AttachmentStatus.Failed, Error = a.Reason }),

        RemoveAttachment a => state with
        {
            AttachmentsByTopic = state.AttachmentsByTopic.With(
                a.TopicId,
                (IReadOnlyList<ComposerAttachment>)state.For(a.TopicId)
                    .Where(x => x.LocalId != a.LocalId)
                    .ToList())
        },

        ClearAttachments a => state with
        {
            AttachmentsByTopic = state.AttachmentsByTopic.With(
                a.TopicId,
                (IReadOnlyList<ComposerAttachment>)state.For(a.TopicId)
                    .Where(x => !a.LocalIds.Contains(x.LocalId))
                    .ToList())
        },

        _ => state
    };

    private static ComposerState Map(
        ComposerState state, string topicId, string localId, Func<ComposerAttachment, ComposerAttachment> transform)
    {
        var attachments = state.For(topicId);
        if (attachments.All(x => x.LocalId != localId))
        {
            return state;
        }

        return state with
        {
            AttachmentsByTopic = state.AttachmentsByTopic.With(
                topicId,
                (IReadOnlyList<ComposerAttachment>)attachments
                    .Select(x => x.LocalId == localId ? transform(x) : x)
                    .ToList())
        };
    }
}