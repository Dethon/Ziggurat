using System.Text.Json.Nodes;
using Accounts.Configuration;
using Domain.DTOs.Admin;
using Domain.Editing;

namespace Accounts.Admin;

// How an admin route answers a section's save, and what it warns of when the save's readers were not
// told, the same for every section.
public static class AdminSections
{
    // A change stood, but the household's readers were not told: they read it again once what they
    // kept runs out.
    public const string NotToldWarning =
        "Saved, but the household's agents could not be told just now: they see the change within a minute.";

    // A section's rules refused it in their own words (nothing changed, nothing recorded), or there is
    // no such household; what a save that stood records and answers is the route's.
    public static async Task<IResult> AnswerAsync(SectionSave save, string notSaved, Func<SectionSave.Saved, Task<IResult>> saved) =>
        save switch
        {
            SectionSave.Invalid invalid => Results.BadRequest(new AdminRefusal(notSaved, invalid.Problems)),
            SectionSave.Saved stood => await saved(stood),
            _ => Results.NotFound()
        };

    public static string? Warning(SectionSave.Saved saved) => saved.NotTold is null ? null : NotToldWarning;

    // What an admin page sent, laid over the section as it is now when the page said what it loaded
    // (AdminEdit), so another owner's change since then stands; sent without, it is saved as sent.
    public static MergedEdit Merged(
        JsonObject sent, JsonObject current, IReadOnlyDictionary<string, string>? arrayKeys = null)
    {
        if (!sent.Remove(AdminEdit.Loaded, out var loaded) || loaded is null)
        {
            return new MergedEdit(sent, []);
        }

        return EditMerge.Merge(loaded, sent, current, arrayKeys ?? new Dictionary<string, string>());
    }

    // A field this page and another owner both changed, to different values: nothing is saved, and
    // the owner is told what to look at again.
    public static IResult Conflict(string notSaved, MergedEdit merged, Func<IReadOnlyList<string>, string> named)
    {
        var what = merged.Conflicts.Select(named).Distinct(StringComparer.Ordinal).ToList();
        var list = what.Count == 1 ? what[0] : $"{string.Join(", ", what[..^1])} and {what[^1]}";
        return Results.Conflict(new AdminRefusal(
            $"{notSaved} Someone else changed {list} since this page was opened, and so did you: reload to see their change, then make yours again.",
            what));
    }
}