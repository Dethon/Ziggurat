using Accounts.Configuration;
using Domain.DTOs.Admin;

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
}