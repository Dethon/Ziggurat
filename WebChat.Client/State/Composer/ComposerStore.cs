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

// What the browser reports as a dictation runs. It owns the microphone, the encoder, the gesture
// thresholds and the upload, and calls in only at decisions.
public record DictationStarted : IAction;

public record DictationLatched : IAction;

// The recording is over and the words are on their way.
public record DictationEnded : IAction;

public record DictationTranscribed(string Text) : IAction;

// Thrown away: slid to discard, the trash button, Escape, a topic change, or a hidden tab. No
// request was made and nothing reaches the composer.
public record DictationDiscarded : IAction;

public record DictationFailed(string Reason) : IAction;

// The microphone cannot be used at all here — permission refused, or a browser without the APIs.
// The control stops trying for the session.
public record DictationUnavailable(string Reason) : IAction;

// A press too short to be a hold. Nothing was recorded and nothing is being said about failure.
public record DictationMisTapped(string Hint) : IAction;

// The two ways a latched dictation ends, and the way any dictation ends early. The effect is what
// reaches the microphone; the state moves when the browser reports back.
public record StopDictation : IAction;

public record DiscardDictation : IAction;

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

        // A new dictation clears whatever the last one left on screen: a refusal about a recording
        // that no longer exists is noise the moment a new one starts.
        DictationStarted => Dictating(state, DictationStatus.Recording),

        DictationLatched => Dictating(state, DictationStatus.Latched),

        DictationEnded => state with
        {
            Dictation = state.Dictation with { Status = DictationStatus.Transcribing }
        },

        DictationTranscribed a => state with
        {
            Dictation = state.Dictation with
            {
                Status = DictationStatus.Idle,
                Transcript = new PendingTranscript(a.Text, Stamp())
            }
        },

        DictationDiscarded => state with
        {
            Dictation = state.Dictation with { Status = DictationStatus.Idle }
        },

        DictationFailed a => state with
        {
            Dictation = state.Dictation with { Status = DictationStatus.Idle, Refusal = a.Reason }
        },

        DictationUnavailable a => state with
        {
            Dictation = state.Dictation with
            {
                Status = DictationStatus.Idle,
                Unavailable = true,
                Refusal = a.Reason
            }
        },

        DictationMisTapped a => state with
        {
            Dictation = state.Dictation with { Status = DictationStatus.Idle, Hint = a.Hint }
        },

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

    private static ComposerState Dictating(ComposerState state, DictationStatus status) => state with
    {
        Dictation = state.Dictation with { Status = status, Refusal = null, Hint = null }
    };

    // Monotonic within the session, which is all the composer needs to tell one transcript from
    // the next one carrying the same words.
    private static long Stamp() => Interlocked.Increment(ref _stamp);

    private static long _stamp;

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