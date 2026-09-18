using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Judgments;

namespace Domain.Tools.Web;

// One of an overlay's visible buttons or links, by the accessible-name approximation the
// dismisser's text path already computes. The index is the control's position in the list the
// names were read from, so a pick clicks by index into that same list.
public sealed record ModalControl(int Index, string Role, string Name);

public enum ModalPickStatus
{
    // A control to click, at the bar.
    Picked,

    // The judge answered and nothing is to be clicked: `none`, a pick under the bar, or a pick
    // naming no control that was sent.
    None,

    // No answer: deadline, error, or a judge with no key.
    Absent,

    // An overlay kind with no questions — nothing was sent.
    NotAsked
}

public sealed record ModalPick(ModalPickStatus Status, int? Index, double? Confidence, TimeSpan Latency)
{
    public static readonly ModalPick NotAsked = new(ModalPickStatus.NotAsked, null, null, TimeSpan.Zero);
}

// Asks Jev which of an overlay's controls does what that kind of overlay wants closed with. A
// cookie wall is asked two questions and a confident reject is taken first, so the wall closes
// with the fewest cookies where the page offers it and still closes where it does not. Every
// other kind is asked one. `none` is never clicked, nor is anything under the bar, nor a late or
// absent answer — each of those leaves the page exactly as today.
//
// The state carries the kind and the controls alone: no url, no page text. Jev's accuracy falls
// with irrelevant state, and a page's words are nobody's business but the model's.
public sealed class ModalJudge(IJudge judge, ModalJudgmentSettings settings, TimeProvider timeProvider)
{
    public const string NoneChoice = "none";

    // The cap the caller lists controls up to, so the list read from the page and the list sent
    // are the same one.
    public int MaxControls => settings.MaxControls;

    public const string RejectQuestionId = "reject";
    public const string AcceptQuestionId = "accept";
    public const string EnterQuestionId = "enter";
    public const string DeclineQuestionId = "decline";
    public const string DenyQuestionId = "deny";

    private const string NoneCriterion = "No listed control does this.";

    // The wording the probe measured (probe/README.md): the two cookie questions each say that a
    // control opening settings or more information does not close the wall, which is what turned
    // "Manage preferences" from a literal reading of "fewest cookies" into a `none`.
    private static string Framing(string overlay) =>
        $"`controls` are the visible buttons and links of {overlay} shown over a web page, " +
        "each with its `index`, `role` and `name`.";

    private const string SettingsDoNotClose =
        " A control that opens settings, preferences or more information does not close the wall.";

    private const string NoneIfNone = " Answer `none` if no listed control does.";

    public static readonly string RejectInstructions =
        Framing("a cookie consent wall") +
        " Which one control closes the wall refusing all optional cookies or keeping only the necessary ones?" +
        SettingsDoNotClose + NoneIfNone;

    public static readonly string AcceptInstructions =
        Framing("a cookie consent wall") +
        " Which one control closes the wall accepting or acknowledging the cookies?" +
        SettingsDoNotClose + NoneIfNone;

    public static readonly string EnterInstructions =
        Framing("an age gate") +
        " Which one control confirms the person is an adult and enters the site?" + NoneIfNone;

    public static readonly string DeclineInstructions =
        Framing("a newsletter or sign-up popup") +
        " Which one control closes or declines the popup without subscribing?" + NoneIfNone;

    public static readonly string DenyInstructions =
        Framing("a request to send browser notifications") +
        " Which one control declines or dismisses the request to send notifications?" + NoneIfNone;

    // The questions a kind is asked, in the order the rule consults them. Generic has none: it
    // names no overlay the container selectors detect, so there is nothing to ask.
    public static IReadOnlyList<(string Id, string Instructions)> QuestionsFor(ModalType kind) => kind switch
    {
        ModalType.CookieConsent => [(RejectQuestionId, RejectInstructions), (AcceptQuestionId, AcceptInstructions)],
        ModalType.AgeGate => [(EnterQuestionId, EnterInstructions)],
        ModalType.Newsletter => [(DeclineQuestionId, DeclineInstructions)],
        ModalType.Notification => [(DenyQuestionId, DenyInstructions)],
        _ => []
    };

    public async Task<ModalPick> PickAsync(ModalType kind, IReadOnlyList<ModalControl> controls, CancellationToken ct)
    {
        var request = Ask(kind, controls, settings.MaxControls);
        if (request is null)
        {
            return ModalPick.NotAsked;
        }

        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.DeadlineMs), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var outcome = await judge.JudgeAsync(request, linked.Token);
        var latency = timeProvider.GetElapsedTime(started);

        // An answer that arrives after the deadline is discarded whatever the judge made of the
        // cancellation: the browse has moved on, and a click landing late lands on the wrong page.
        return outcome switch
        {
            _ when deadline.IsCancellationRequested => Absent(latency),
            JudgmentOutcome.Answered answered => Decide(kind, answered.Judgment, request, settings.Confidence, latency),
            _ => Absent(latency)
        };
    }

    // The request, or null for a kind with nothing to ask. Controls past the cap are left out in
    // document order; the criteria name each one by index, plus `none`.
    public static JudgmentRequest? Ask(ModalType kind, IReadOnlyList<ModalControl> controls, int maxControls)
    {
        var questions = QuestionsFor(kind);
        if (questions.Count == 0)
        {
            return null;
        }

        var sent = controls.Take(maxControls).ToList();
        var criteria = sent
            .Select(c => KeyValuePair.Create(c.Index.ToString(), $"{c.Role} \"{c.Name}\""))
            .Append(KeyValuePair.Create(NoneChoice, NoneCriterion))
            .ToDictionary(StringComparer.Ordinal);

        var state = new JsonObject
        {
            ["overlay_kind"] = ModalKinds.Of(kind),
            ["controls"] = new JsonArray(sent.Select(c => (JsonNode)new JsonObject
            {
                ["index"] = c.Index,
                ["role"] = c.Role,
                ["name"] = c.Name
            }).ToArray())
        };

        return new JudgmentRequest(
            state,
            questions.ToDictionary(
                q => q.Id,
                q => (JudgmentQuestion)new ChoiceQuestion(q.Instructions, criteria),
                StringComparer.Ordinal));
    }

    // The rule: the questions in their order, the first confident pick of a listed control wins,
    // and `none` is never a pick. A judge that answered something other than a choice to a
    // question it was asked is an absence, not a `none`.
    public static ModalPick Decide(ModalType kind, Judgment judgment, JudgmentRequest request, double bar, TimeSpan latency)
    {
        var answers = QuestionsFor(kind)
            .Select(q => judgment.Answers.GetValueOrDefault(q.Id) as ChoiceAnswer)
            .ToList();
        if (answers.Any(a => a is null))
        {
            return Absent(latency);
        }

        var listed = request.Questions.Values.OfType<ChoiceQuestion>().First().Criteria.Keys;
        var confident = answers
            .OfType<ChoiceAnswer>()
            .FirstOrDefault(a => a.Confidence >= bar
                                 && !string.Equals(a.Choice, NoneChoice, StringComparison.Ordinal)
                                 && listed.Contains(a.Choice)
                                 && int.TryParse(a.Choice, out _));

        return confident is not null
            ? new ModalPick(ModalPickStatus.Picked, int.Parse(confident.Choice), confident.Confidence, latency)
            : new ModalPick(ModalPickStatus.None, null, answers.OfType<ChoiceAnswer>().Max(a => a.Confidence), latency);
    }

    private static ModalPick Absent(TimeSpan latency) => new(ModalPickStatus.Absent, null, null, latency);
}