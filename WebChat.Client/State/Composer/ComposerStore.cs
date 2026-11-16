using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.State.AgentSettings;

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

// Words handed to the composer from elsewhere — a fork's, from the message it was forked from — which
// land in the box as a transcript does, for the person to send.
public record OfferComposerText(string Text) : IAction;

// Queued messages given back to their sender's composer on a topic — one taken back to be edited —
// in the order they were queued: their words before whatever is typed there, each joined by a blank
// line, and their files back as ready attachments, already uploaded.
public record ReturnToComposer(string TopicId, IReadOnlyList<ReturnedMessage> Messages) : IAction;

// The composer's send that carried a returned message's patch went out: the picker's choice is next.
public record ReturnedPatchSent(string TopicId, ReturnedPatch Sent) : IAction;

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

        OfferComposerText a => state with
        {
            Dictation = state.Dictation with { Transcript = new PendingTranscript(a.Text, Stamp()) }
        },

        ReturnToComposer a => Returned(state, a),

        // A pick is the person's choice for whatever they send next, returned words included.
        SetAgentModel or SetAgentReasoningEffort => state with { ReturnedPatches = new Dictionary<string, ReturnedPatch>() },

        ReturnedPatchSent a when state.ReturnedPatches.GetValueOrDefault(a.TopicId) == a.Sent => state with
        {
            ReturnedPatches = state.ReturnedPatches.Without(a.TopicId)
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

    private static ComposerState Returned(ComposerState state, ReturnToComposer returned)
    {
        var text = string.Join(
            ComposerSelectors.ReturnedSeparator,
            returned.Messages.Select(m => m.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        IReadOnlyList<ComposerAttachment> files =
        [
            .. state.For(returned.TopicId),
            .. returned.Messages.SelectMany(m => m.Attachments).Select(reference => new ComposerAttachment
            {
                LocalId = Guid.NewGuid().ToString("N"),
                FileName = reference.FileName,
                MediaType = reference.MediaType,
                SizeBytes = reference.SizeBytes,
                Status = AttachmentStatus.Ready,
                PercentComplete = 100,
                Reference = reference
            })
        ];

        // The words merge into one message, which opens with the earliest returned, so that one's
        // patch is the one it goes out on, as is any still waiting from an earlier return.
        var patches = state.ReturnedPatches.ContainsKey(returned.TopicId) || returned.Messages.Count == 0
            ? state.ReturnedPatches
            : state.ReturnedPatches.With(returned.TopicId, new ReturnedPatch(returned.Messages[0].ConfigPatch));

        return state with
        {
            ReturnedPatches = patches,
            AttachmentsByTopic = state.AttachmentsByTopic.With(returned.TopicId, files),
            Dictation = text.Length == 0
                ? state.Dictation
                : state.Dictation with { Transcript = new PendingTranscript(text, Stamp(), TextPlacement.Before) }
        };
    }

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