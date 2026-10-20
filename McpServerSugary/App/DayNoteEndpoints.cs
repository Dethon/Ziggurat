using System.Globalization;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record DayNoteDto(DateOnly Day, string Text);

public record SaveDayNoteRequest(string? Text);

// A note on a day of her calendar. The day is the note's key, so writing one sets it and emptying
// it deletes it: there is never a day with an empty note.
public static class DayNoteEndpoints
{
    public const int MaxLength = 500;

    public static void MapDayNoteEndpoints(this IEndpointRouteBuilder api)
    {
        var notes = api.MapGroup("/day-notes");
        notes.MapGet("/", ListAsync);
        notes.MapPut("/{day}", SetAsync);
        notes.MapDelete("/{day}", DeleteAsync);
    }

    // Oldest first, and bounded to the days [from, to], both included, when the calendar asks for a
    // month.
    private static async Task<IReadOnlyList<DayNoteDto>> ListAsync(
        SugaryDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null)
    {
        var lower = from ?? DateOnly.MinValue;
        var upper = to ?? DateOnly.MaxValue;
        var rows = await db.DayNotes.AsNoTracking()
            .Where(n => n.Day >= lower && n.Day <= upper)
            .OrderBy(n => n.Day)
            .ToListAsync(ct);
        return [.. rows.Select(n => new DayNoteDto(n.Day, n.Text))];
    }

    private static async Task<IResult> SetAsync(
        string day, SaveDayNoteRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        if (Problem(day, text, calendar) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var key = Parse(day)!.Value;
        var note = await db.DayNotes.FindAsync([key], ct);
        if (text == "")
        {
            if (note is not null)
            {
                db.DayNotes.Remove(note);
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }

        if (note is null)
        {
            note = new DayNote { Day = key };
            db.DayNotes.Add(note);
        }

        note.Text = text;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DayNoteDto(note.Day, note.Text));
    }

    private static async Task<IResult> DeleteAsync(string day, SugaryDbContext db, CancellationToken ct) =>
        Parse(day) is not { } key
            ? Results.Problem(NotADay, statusCode: StatusCodes.Status400BadRequest)
            : await db.DayNotes.Where(n => n.Day == key).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent()
                : Results.NotFound();

    private const string NotADay = "A day is written as yyyy-MM-dd.";

    private static string? Problem(string day, string text, HomeCalendar calendar) => Parse(day) switch
    {
        null => NotADay,
        { } key when key > calendar.Today.AddDays(PeriodRules.FutureSlackDays) => "A note can't be for a day after today.",
        _ when text.Length > MaxLength => $"A note can be at most {MaxLength} characters.",
        _ => null
    };

    private static DateOnly? Parse(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}