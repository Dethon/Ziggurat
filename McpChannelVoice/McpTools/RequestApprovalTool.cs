using System.ComponentModel;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.Judgments;
using McpChannelVoice.Services;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval via voice")]
    public static Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        // The asking turn, from the context the agent stamps on this hop: the reader hands it to
        // the judge's client, which sends nothing for the local box and bills the rest to it, and
        // whose question it is says whose spoken answer counts.
        var turn = ConversationScope.Parse(context.Params?.Meta);
        return RunAsync(
            conversationId, mode, requests, JudgmentCaller.For(turn), services, cancellationToken,
            Principal.Of(turn)?.Account);
    }

    // Being called off is how a prompt still waiting learns its question was answered elsewhere —
    // on Telegram, in the chat client — so it stops asking: the token ends the spoken prompt and
    // the open microphone alike, and nothing more is said.
    public static async Task<string> RunAsync(
        string conversationId,
        ApprovalMode mode,
        IReadOnlyList<ToolApprovalRequest> requests,
        JudgmentCaller caller,
        IServiceProvider services,
        CancellationToken cancellationToken = default,
        Account? asker = null)
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
            // Nobody could be asked: no answer, which ends it unless its asker is offered it elsewhere.
            return p.Mode == ApprovalMode.Notify ? "notified" : ChannelProtocol.NoAnswer;
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
        var reader = services.GetRequiredService<IApprovalReader>();
        var voices = asker is null ? null : services.GetService<ISpeakerVerifier>();

        var toolList = string.Join(", ", p.Requests.Select(r => r.ToolName.Split("__").Last()));
        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        // Why the exec screen asked, spoken once before the question and never folded into it: the
        // reader is handed the question alone, because that is the prompt its probe measured, and
        // the re-ask repeats the question rather than the reason.
        var reason = ScreenReason(p.Requests);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var spoken = attempt == 1 && reason is not null ? $"{reason} {prompt}" : prompt;
            if (!await SpeakAndAwaitAsync(session, spoken, tts, settings, cancellationToken))
            {
                // Satellite disconnected mid-approval; abandon rather than opening a capture on a
                // dead session that would block until the request is cancelled. Nobody heard it.
                return ChannelProtocol.NoAnswer;
            }

            // The same gate the wake turn the user is answering was endpointed against, room-noise
            // cap included: a confirmation mic that behaved differently from the mic that heard the
            // question cut people off mid-answer.
            var answer = await CaptureAnswerAsync(
                session, gates.Create(session.SatelliteId, session.Config),
                stt, settings, time, asker is null ? null : (voices, asker), cancellationToken);
            if (answer is null)
            {
                // Arbitration stole the turn mid-answer: the arbiter already re-armed this
                // satellite via pause-satellite, so there is no one left here to re-prompt.
                return ChannelProtocol.NoAnswer;
            }

            if (answer.NotTheAsker)
            {
                // Somebody answered, and it was not the person whose question it is: asked again, as
                // an answer not understood is, and never taken as theirs.
                prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
                continue;
            }
            // Read for what it means, against the prompt as it was spoken: the re-ask's wording is
            // the prompt the person is answering.
            var reading = await reader.ReadAsync(prompt, answer.Text, caller, cancellationToken);

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                Outcome = reading.Response.ToString(),
                DecidedBy = ApprovalDeciders.Of(reading.DecidedBy),
                ApprovedProbability = reading.Approved,
                DeclinedProbability = reading.Declined,
                DurationMs = (long)reading.Latency.TotalMilliseconds,
                ConversationId = p.ConversationId
            }.About(session));

            switch (reading.Response)
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

    // One short sentence, for the first reason the screen gave: a spoken prompt is waited on in
    // silence, and the first code is the one that matters most.
    internal static string? ScreenReason(IReadOnlyList<ToolApprovalRequest> requests) =>
        requests.SelectMany(r => r.Screen ?? []).Select(code => code switch
            {
                ExecScreenCodes.NotRequested => "Esto no parece parte de lo que pediste.",
                ExecScreenCodes.Destructive => "Borraría o cambiaría algo que ya existe.",
                ExecScreenCodes.SendsOut => "Enviaría datos a un servidor.",
                ExecScreenCodes.RunsDownloaded => "Descargaría un programa y lo ejecutaría.",
                ExecScreenCodes.Unjudged => "Se ejecuta en tu ordenador y no he podido comprobarlo.",
                ExecScreenCodes.AsPerson => "Entraría en esa web con tu cuenta.",
                _ => null
            })
            .FirstOrDefault(sentence => sentence is not null);

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

    // What was heard, and whether the voice that said it was conclusively another enrolled person's,
    // or one nobody enrolled: not the asker's, so never taken as their answer.
    private sealed record HeardAnswer(string Text, bool NotTheAsker = false);

    // Returns null when arbitration abandoned the capture — distinct from an empty answer,
    // which re-prompts — and an answer not the asker's for a voice that is not. A voice too short to
    // judge, or a verifier with nothing to judge it by, is the asker's: they were recognised at this
    // satellite a moment ago, and a one-word answer is all a person gives.
    // Holds the session's microphone, not the session as a turn: an approval prompt is a question
    // the agent asked mid-turn, so it must not mark a turn start or a speech end on the playback
    // queue — that would corrupt the latency reported for the turn actually in flight.
    private static async Task<HeardAnswer?> CaptureAnswerAsync(
        SatelliteSession session, SilenceGate gate, ISpeechToText stt,
        VoiceSettings settings, TimeProvider time, (ISpeakerVerifier? Voices, Account Asker)? whose,
        CancellationToken ct)
    {
        var mic = session.Mic;
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
            return new HeardAnswer(string.Empty);
        }

        if (whose is ({ } voices, var asker))
        {
            var heard = await voices.VerifyAsync(capture.BufferedAudio, capture.Stats.SpeechMs, session.Config, ct);
            if (heard.Decision == SpeakerDecision.Rejected
                || heard is { Decision: SpeakerDecision.Accepted, IdentifiedSpeaker: { } speaker } && speaker != asker.UserId)
            {
                return new HeardAnswer(string.Empty, NotTheAsker: true);
            }
        }

        var result = await stt.TranscribeAsync(capture.Audio, new TranscriptionOptions(), ct);
        return new HeardAnswer(result.Text ?? string.Empty);
    }
}