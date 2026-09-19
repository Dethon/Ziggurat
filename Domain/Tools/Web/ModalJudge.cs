using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Channels;
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
        // Settings a judgment cannot be asked under, treated as nothing to ask rather than acted
        // on: a deadline that is not a wait threw out of the CancellationTokenSource constructor
        // below, and the dismisser's catch turned that into a silent left-standing forever.
        if (settings.DeadlineMs <= 0 || settings.MaxControls <= 0)
        {
            return ModalPick.NotAsked;
        }

        // A turn addressed to the local box sends nothing to a hosted judge, and a wall's buttons
        // are the page the person is reading. The wall is left to the model, as with no key.
        if (LemonadeModelId.IsLemonade(CallerContext.Current?.ConfigPatchModel))
        {
            return ModalPick.NotAsked;
        }

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

    // A button's label, not a paragraph. An accessible name is the control's whole textContent, so
    // a consent manager's vendor blurb or a notification tray's message preview arrives here in
    // full; past this it stops being a label and starts being the page's words, which the state
    // promises not to carry. Long enough for any real button in either language.
    public const int MaxNameLength = 120;

    // Names that describe acting on the person's own data rather than closing a wall. The
    // container selectors are substring matches, so a `confirm-modal` with Delete and Keep reads
    // as a newsletter; nothing on this path can undo a POST, and the go-back only covers a
    // navigation. A control named like this is never offered, so it can never be picked — which
    // also means a page cannot name a button into being clicked.
    private static readonly string[] _destructiveNames =
    [
        "delete", "eliminar", "borrar", "remove", "erase",
        "buy", "comprar", "pay", "pagar", "purchase", "checkout", "order now",
        "confirm payment", "confirmar pago", "subscribe and", "suscribirme y",
        "transfer", "transferir", "send money", "enviar dinero",
        "deactivate", "desactivar", "close account", "cerrar cuenta", "unsubscribe all"
    ];

    private static bool IsDestructive(string name) =>
        _destructiveNames.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase));

    // Cut to a label, and quotes escaped: the criteria are prose that the page contributes a
    // substring of, so a name closing its own quote could write an instruction beside it.
    private static string Label(string name)
    {
        var cut = name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
        return cut.Replace("\"", "'", StringComparison.Ordinal);
    }

    // The request, or null for a kind with nothing to ask — or nothing left to ask about. Controls
    // past the cap are left out in document order; the criteria name each one by index, plus
    // `none`. What the judge is shown is what the pick clicks, so the filtering and the cutting
    // both happen here, before either list exists.
    public static JudgmentRequest? Ask(ModalType kind, IReadOnlyList<ModalControl> controls, int maxControls)
    {
        var questions = QuestionsFor(kind);
        if (questions.Count == 0)
        {
            return null;
        }

        var sent = controls
            .Where(c => !IsDestructive(c.Name))
            .Take(maxControls)
            .Select(c => c with { Name = Label(c.Name) })
            .ToList();
        if (sent.Count == 0)
        {
            return null;
        }

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
            .Where(a => a.Confidence >= bar && listed.Contains(a.Choice))
            .Select(a => (Answer: a, Index: int.TryParse(a.Choice, out var index) ? index : (int?)null))
            .FirstOrDefault(a => a.Index is not null);

        // On a `none` the confidence reported is the judge's confidence in that nothing.
        return confident.Answer is not null
            ? new ModalPick(ModalPickStatus.Picked, confident.Index, confident.Answer.Confidence, latency)
            : new ModalPick(ModalPickStatus.None, null, answers.OfType<ChoiceAnswer>().Max(a => a.Confidence), latency);
    }

    private static ModalPick Absent(TimeSpan latency) => new(ModalPickStatus.Absent, null, null, latency);
}