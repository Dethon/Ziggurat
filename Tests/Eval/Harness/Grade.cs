using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tests.Eval.Harness;

// One dimension of how good an answer was, scored from nothing to Scale. Declared beside a
// scenario's contract and never part of it: a pass says the run stayed inside what the scenario
// tolerates, a grade says how much of the answer was there. Two kinds, because two things are
// being asked. Whether the planted facts came back is a count, and a judge paid to count is a
// judge that can miscount; whether the answer says where each fact came from is not, and that is
// what the judge is for.
public abstract record Grade(string Dimension)
{
    public const int Scale = 10;

    public static Grade Facts(string dimension, params SpokenValue[] carries) =>
        new FactGrade(dimension, carries);

    public static Grade Judged(string dimension, string rubric) => new JudgedGrade(dimension, rubric);

    public static Grade Grounded(string dimension, params Source[] pages) => new GroundedGrade(dimension, pages);
}

// The share of the planted values the reply carries, and nothing else. A wrong value beside a
// right one costs nothing here, deliberately: "the search result says 184, the report says 237"
// is the best answer there is, and a count that punished the 184 would punish the reply for
// showing its work. Whether a reply believed the trap is a judgement, and is asked as one.
public sealed record FactGrade(string Dimension, IReadOnlyList<SpokenValue> Carries) : Grade(Dimension)
{
    public double Score(string reply) =>
        Carries.Count == 0
            ? 0
            : (double)Scale * Carries.Count(value => ReplyChecks.Mentions(reply, value)) / Carries.Count;
}

// What a list cannot say, put to the judge with a rubric that carries the ground truth: the
// judge never sees a page, only the reply and the calls, so whatever is true has to be in the
// question. The corpus holds still, which is what makes writing it there safe.
public sealed record JudgedGrade(string Dimension, string Rubric) : Grade(Dimension);

// A page a right answer rests on: where it is served, and what it is called. A reply cites it
// by either — a link, or the title a person would say — and a page counts as opened only at
// exactly this path.
public sealed record Source(string Path, params string[] Titles);

// The share of the pages a right answer rests on that the reply names and somebody opened.
// Counted rather than judged: put to the judge, this dimension came back nine or ten for every
// reply it was shown on every rung, at a third of the judge's bill. What a count can say is
// narrower than what the judge was asked — it cannot tell which figure a link stands behind —
// and it is the part that separates replies: a source named off a search snippet and never
// opened, and a page read and then left out of the answer, both count for nothing.
public sealed record GroundedGrade(string Dimension, IReadOnlyList<Source> Pages) : Grade(Dimension)
{
    public double Score(Recording recording)
    {
        var opened = Opened(recording);

        return Pages.Count == 0
            ? 0
            : (double)Scale * Pages.Count(page => opened.Contains(page.Path) && Cites(recording.Reply, page)) / Pages.Count;
    }

    // The path with nothing of a longer one after it — every page of a site starts with the
    // site's first path — or any of the page's titles.
    private static bool Cites(string reply, Source page) =>
        Regex.IsMatch(reply, Regex.Escape(page.Path) + @"(?![\w/\-])")
        || page.Titles.Any(title => reply.Contains(title, StringComparison.OrdinalIgnoreCase));

    // Every page anybody opened, the agent or a worker it handed the reading to, by the exact
    // path of the url the call named. Whatever tool carried it: the url is what was opened.
    private static HashSet<string> Opened(Recording recording) =>
        recording.Calls.Concat(recording.WorkerCalls)
            .Select(call => Url(call.Arguments))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static string? Url(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) is JsonObject node
                   && node["url"] is JsonValue value
                   && value.TryGetValue<string>(out var url)
                   && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? parsed.AbsolutePath.TrimEnd('/') is { Length: > 0 } path ? path : "/"
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// One run's score on one dimension. The reason is the judge's sentence where a judge gave one.
public sealed record GradeMark(string Dimension, double Score, string? Reason = null);