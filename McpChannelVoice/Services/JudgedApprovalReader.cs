using System.Text.Json.Nodes;
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

    // The wording the probe measured (probe/2026-09-18-answers.txt): "exactly that, all of it"
    // is what sent both narrowed answers to the re-ask instead of the approval.
    private const string Preamble =
        "`prompt` was spoken aloud asking for a yes or no about doing exactly what it names. " +
        "`answer` is the transcribed spoken reply. ";

    public static readonly string ApprovedInstructions =
        Preamble + "Did the person give permission to do exactly that, all of it, without changing or narrowing it?";

    public static readonly string DeclinedInstructions =
        Preamble + "Did the person refuse it, or tell the assistant not to do it as asked?";

    public async Task<ApprovalReading> ReadAsync(string prompt, string answer, CancellationToken ct)
    {
        var wordList = ApprovalGrammarParser.Parse(answer);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(answer))
        {
            return ByWordList(wordList, TimeSpan.Zero);
        }

        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.DeadlineMs), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        // Raced against the deadline rather than awaited and then checked: the person is standing
        // at the satellite, and a judge that ignores its token must not keep them there.
        JudgmentOutcome outcome;
        try
        {
            outcome = await judge.JudgeAsync(Ask(prompt, answer), linked.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new JudgmentOutcome.Absent(AbsenceReason.Deadline);
        }

        var latency = timeProvider.GetElapsedTime(started);
        return outcome is JudgmentOutcome.Answered answered && Nouls(answered.Judgment) is { } nouls
            ? Decide(nouls.Approved, nouls.Declined, wordList, latency)
            : ByWordList(wordList, latency);
    }

    public static JudgmentRequest Ask(string prompt, string answer) => new(
        new JsonObject { ["prompt"] = prompt, ["answer"] = answer },
        new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            [ApprovedQuestionId] = new NoulQuestion(ApprovedInstructions),
            [DeclinedQuestionId] = new NoulQuestion(DeclinedInstructions)
        });

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
            _ when approved >= settings.Sure && declined <= settings.Counter =>
                (ApprovalResponse.Approved, ApprovalDecider.Judgment),
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