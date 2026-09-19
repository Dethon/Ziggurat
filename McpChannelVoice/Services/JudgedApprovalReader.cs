using System.Text.Json.Nodes;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Asks Jev two yes/no questions about the answer and acts by the spec's rule
// (.scratch/jev-voice-approval/spec.md § Decisions): the judge alone when it is sure; the judge
// and the word list together when the judge leans and the list leans the same way; a re-ask for
// everything else. The word list stays the whole answer when the judge is off, absent or late.
//
// Two questions rather than one, because a narrowed answer — "sí, pero la de la cocina no" — is
// neither a yes nor a no to the whole, and one probability cannot say so: it comes back as a low
// yes beside a middling no, which is what keeps it out of both sure bars and out of agreement.
public sealed class JudgedApprovalReader(IJudge judge, ApprovalJudgmentSettings settings, TimeProvider timeProvider)
    : IApprovalReader
{
    public const string ApprovedQuestionId = "approved";
    public const string DeclinedQuestionId = "declined";

    // The wording the probe measured (probe/2026-09-18-wordings-*.txt, "goahead-asasked"). The
    // spec's first draft asked for "exactly that, all of it, without changing or narrowing it",
    // and Jev read a bare "adelante" or "hazlo" as not quite that — 0.83 to 0.89, under the sure
    // bar, so the answers the word list is blind to all went to the re-ask. Asking for permission
    // "to go ahead with it as asked" and naming the narrowed yes as the exception keeps those at
    // 0.91 and above while "sí, pero la de la cocina no" stays under 0.1.
    private const string Preamble =
        "`prompt` was spoken aloud asking for a yes or no about doing exactly what it names. " +
        "`answer` is the transcribed spoken reply. ";

    public static readonly string ApprovedInstructions =
        Preamble + "Did the person give permission to go ahead with it as asked? " +
        "A yes that changes or narrows what was asked is not permission.";

    public static readonly string DeclinedInstructions =
        Preamble + "Did the person refuse it, or tell the assistant not to do it as asked?";

    public async Task<ApprovalReading> ReadAsync(string prompt, string answer, string? turnModel, CancellationToken ct)
    {
        // Before anything else, because an already-cancelled token never raises on the await path
        // below: the judge answers an immediate absence rather than throwing, and the word list
        // would then decide a turn that is being torn down. Same rule as the catch filter's.
        ct.ThrowIfCancellationRequested();

        var wordList = ApprovalGrammarParser.Parse(answer);
        if (!settings.Enabled || !Usable(settings) || string.IsNullOrWhiteSpace(answer))
        {
            return ByWordList(wordList, TimeSpan.Zero);
        }

        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.DeadlineMs), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        // Raced against the deadline rather than awaited and then checked: the person is standing
        // at the satellite, and a judge that ignores its token must not keep them there. The
        // caller's own cancellation is not a late judge — the turn is being torn down, and a word
        // list verdict now would approve something on a turn nobody is waiting for.
        JudgmentOutcome outcome;
        try
        {
            outcome = await judge.JudgeAsync(Ask(prompt, answer, turnModel), linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = new JudgmentOutcome.Absent(AbsenceReason.Deadline);
        }

        var latency = timeProvider.GetElapsedTime(started);
        return outcome is JudgmentOutcome.Answered answered && Nouls(answered.Judgment) is { } nouls
            ? Decide(nouls.Approved, nouls.Declined, wordList, latency)
            : ByWordList(wordList, latency);
    }

    // Bars a judgment cannot be read against, treated as the feature off rather than acted on. A
    // deadline that is not a wait threw out of the CancellationTokenSource constructor on every
    // approval; a Counter at or above Sure made the first arm true for every answer, so a spoken
    // "no" approved. Both are config errors, and the word list is what this falls back to anyway.
    private static bool Usable(ApprovalJudgmentSettings settings) =>
        settings.DeadlineMs > 0 && settings.Counter < settings.Sure;

    public static JudgmentRequest Ask(string prompt, string answer, string? turnModel) => new(
        new JsonObject { ["prompt"] = prompt, ["answer"] = answer },
        new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            [ApprovedQuestionId] = new NoulQuestion(ApprovedInstructions),
            [DeclinedQuestionId] = new NoulQuestion(DeclinedInstructions)
        },
        turnModel);

    // A judge that answered something other than both nouls is an absence, not a lean.
    private static (double Approved, double Declined)? Nouls(Judgment judgment) =>
        judgment.Answers.GetValueOrDefault(ApprovedQuestionId) is NoulAnswer approved
        && judgment.Answers.GetValueOrDefault(DeclinedQuestionId) is NoulAnswer declined
            ? (approved.Probability, declined.Probability)
            : null;

    private ApprovalReading Decide(double approved, double declined, ApprovalResponse wordList, TimeSpan latency)
    {
        var (response, decider) = (approved, declined) switch
        {
            // A sure yes acts alone — except over a word list that heard a refusal. That is the one
            // combination where a single misjudgment runs a tool the old code refused, and the
            // person is standing there: a re-ask costs one question on "no hay problema, hazlo" and
            // is the only thing between a hosted model's bad call and an action nobody permitted.
            // The mirror is deliberately absent: a sure no over a word-list yes still declines,
            // because refusing is the safe direction and a narrowed yes is what the list misreads.
            _ when approved >= settings.Sure && declined <= settings.Counter =>
                wordList == ApprovalResponse.Declined
                    ? (ApprovalResponse.Ambiguous, ApprovalDecider.Judgment)
                    : (ApprovalResponse.Approved, ApprovalDecider.Judgment),
            _ when declined >= settings.Sure && approved <= settings.Counter =>
                (ApprovalResponse.Declined, ApprovalDecider.Judgment),
            _ when approved >= settings.Lean && declined <= settings.Counter && wordList == ApprovalResponse.Approved =>
                (ApprovalResponse.Approved, ApprovalDecider.Agreement),
            _ when declined >= settings.Lean && approved <= settings.Counter && wordList == ApprovalResponse.Declined =>
                (ApprovalResponse.Declined, ApprovalDecider.Agreement),
            _ => (ApprovalResponse.Ambiguous, ApprovalDecider.Judgment)
        };

        return new ApprovalReading(response, decider, approved, declined, latency);
    }

    private static ApprovalReading ByWordList(ApprovalResponse wordList, TimeSpan latency) =>
        new(wordList, ApprovalDecider.WordList, null, null, latency);
}