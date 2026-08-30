using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval via voice")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is null)
        {
            return p.Mode == ApprovalMode.Notify ? "notified" : "rejected";
        }

        if (p.Mode == ApprovalMode.Notify)
        {
            // The tool name itself is never narrated. An acknowledgement the agent wrote before
            // this auto-approved call is the turn's preamble, and the preamble is the reply
            // speaker's to deliver — once per turn, under its own playback kind.
            services.GetRequiredService<ReplySpeaker>().SpeakPreamble(session, p.ConversationId);
            return "notified";
        }

        var stt = services.GetRequiredService<ISpeechToText>();
        var gates = services.GetRequiredService<SilenceGateFactory>();
        var time = services.GetRequiredService<TimeProvider>();

        var toolList = string.Join(", ", p.Requests.Select(r => r.ToolName.Split("__").Last()));
        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (!await SpeakAndAwaitAsync(session, prompt, tts, settings, cancellationToken))
            {
                // Satellite disconnected mid-approval; abandon rather than opening a capture on a
                // dead session that would block until the request is cancelled.
                return "rejected";
            }

            // The same gate the wake turn the user is answering was endpointed against, room-noise
            // cap included: a confirmation mic that behaved differently from the mic that heard the
            // question cut people off mid-answer.
            var answer = await CaptureAnswerAsync(
                session.Mic, gates.Create(session.SatelliteId, session.Config),
                stt, settings, time, cancellationToken);
            if (answer is null)
            {
                // Arbitration stole the turn mid-answer: the arbiter already re-armed this
                // satellite via pause-satellite, so there is no one left here to re-prompt.
                return "rejected";
            }
            var parsed = ApprovalGrammarParser.Parse(answer);

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                Outcome = parsed.ToString(),
                ConversationId = p.ConversationId
            }.About(session));

            switch (parsed)
            {
                case ApprovalResponse.Approved:
                    return "approved";
                case ApprovalResponse.Declined:
                    return "rejected";
            }

            prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
        }

        return "rejected";
    }

    private static async Task<bool> SpeakAndAwaitAsync(
        SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings,
        CancellationToken ct)
    {
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Kind: PlaybackKind.Approval,
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(
                text, new SynthesisOptions { Voice = session.ResolveVoice(settings) }, default));

        var ticket = session.Playback.Enqueue(job);
        if (ticket.Refused is not null)
        {
            // The satellite went away between session resolution and the enqueue — signal the caller
            // to abandon the approval instead of opening a capture on a dead session that would
            // block until the request is cancelled.
            return false;
        }

        // The token is this caller's own reason to stop waiting — the agent cancelling the approval
        // request — not a guard against hanging: the queue settles every job it is handed, teardown
        // included.
        await ticket.Completed.WaitAsync(ct);
        return true;
    }

    // Returns null when arbitration abandoned the capture — distinct from an empty answer,
    // which re-prompts.
    // Holds a microphone, not a session: an approval prompt is a question the agent asked mid-turn,
    // so it must not mark a turn start or a speech end on the playback queue — that would corrupt
    // the latency reported for the turn actually in flight. Which type this takes is what says so.
    private static async Task<string?> CaptureAnswerAsync(
        Microphone mic, SilenceGate gate, ISpeechToText stt,
        VoiceSettings settings, TimeProvider time, CancellationToken ct)
    {
        var followUp = settings.FollowUp;
        if (followUp.PlaybackTailMs > 0)
        {
            // Echo guard after the prompt finishes, on the injected clock like FollowUpConversation's.
            await Task.Delay(TimeSpan.FromMilliseconds(followUp.PlaybackTailMs), time, ct);
        }

        var capture = mic.Open(
            gate,
            // The approval mic is an open capture like any wake turn's: Rule B must be able
            // to ask it what it heard during another satellite's wake-word span.
            new ChunkHistory(time, settings.Arbitration.HistorySpan));

        CaptureOutcome outcome;
        try
        {
            outcome = await capture.Completed.WaitAsync(ct);
        }
        finally
        {
            // Always close, even if the wait is cancelled, so a cancelled approval doesn't leave a
            // dangling mic capture routing audio into a dead turn. Closing pays back into the
            // room-noise memory as one act, so an approval mic keeps the memory alive on a
            // satellite used mostly for confirmations.
            mic.Close(capture);
        }

        if (outcome == CaptureOutcome.Abandoned)
        {
            return null;
        }

        if (outcome == CaptureOutcome.NoSpeech)
        {
            return string.Empty;
        }

        var result = await stt.TranscribeAsync(capture.Audio, new TranscriptionOptions(), ct);
        return result.Text ?? string.Empty;
    }
}