using System.Globalization;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record DayNoteDto(DateOnly Day, string Text, string? Flow, IReadOnlyList<string> Symptoms);

// Loaded is what the sheet opened with (StaleEdits). An app from before flow and symptoms sends its
// words alone, no symptoms at all (the app now always sends them, if none an empty list): such a save
// changes the words and keeps the rest.
public record SaveDayNoteRequest(
    string? Text, string? Flow = null, IReadOnlyList<string>? Symptoms = null, SaveDayNoteRequest? Loaded = null);

// What she says of a day of her calendar: her words, her flow, her symptoms. The day is the entry's
// key in their log, so writing one sets it and saying nothing deletes it: there is never a day with
// nothing said.
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
        return [.. rows.Select(ToDto)];
    }

    private static async Task<IResult> SetAsync(
        string day, SaveDayNoteRequest request, SugaryDbContext db, HomeCalendar calendar, HttpContext http,
        CancellationToken ct)
    {
        if (Parse(day) is not { } key)
        {
            return Results.Problem(NotADay, statusCode: StatusCodes.Status400BadRequest);
        }

        // The day is a note's key within one person's log, and the context is that person's.
        var note = await db.DayNotes.FirstOrDefaultAsync(n => n.Day == key, ct);
        var stored = note is null
            ? new SaveDayNoteRequest("", null, [])
            : new SaveDayNoteRequest(note.Text, note.Flow, DayEntries.Split(note.Symptoms));
        if (request.Loaded is { } loaded)
        {
            var merged = StaleEdits.Merge(http, Canonical(loaded), Canonical(request), stored);
            if (merged.Value is not { } value)
            {
                return StaleEdits.Conflict(merged.Conflicts);
            }

            request = value;
        }
        else if (request.Symptoms is null)
        {
            request = stored with { Text = request.Text };
        }

        var text = request.Text?.Trim() ?? "";
        var symptoms = request.Symptoms ?? [];
        if (Problem(key, text, request.Flow, symptoms, calendar) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (text == "" && request.Flow is null && symptoms.Count == 0)
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
        note.Flow = request.Flow;
        note.Symptoms = DayEntries.Join(symptoms);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(note));
    }

    // Compared as kept: words trimmed, symptoms in their order, each once.
    private static SaveDayNoteRequest Canonical(SaveDayNoteRequest request) =>
        new(request.Text?.Trim() ?? "", request.Flow, DayEntries.Ordered(request.Symptoms ?? []));

    private static DayNoteDto ToDto(DayNote note) => new(note.Day, note.Text, note.Flow, DayEntries.Split(note.Symptoms));

    private static async Task<IResult> DeleteAsync(string day, SugaryDbContext db, CancellationToken ct) =>
        Parse(day) is not { } key
            ? Results.Problem(NotADay, statusCode: StatusCodes.Status400BadRequest)
            : await db.DayNotes.Where(n => n.Day == key).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent()
                : Results.NotFound();

    private const string NotADay = "A day is written as yyyy-MM-dd.";

    private static string? Problem(
        DateOnly day, string text, string? flow, IReadOnlyList<string> symptoms, HomeCalendar calendar) =>
        day switch
        {
            _ when day > calendar.Today.AddDays(PeriodRules.FutureSlackDays) => "A note can't be for a day after today.",
            _ when text.Length > MaxLength => $"A note can be at most {MaxLength} characters.",
            _ when flow is not null && !DayEntries.Flows.Contains(flow) => DayEntries.UnknownFlow,
            _ => symptoms.FirstOrDefault(code => !DayEntries.Symptoms.Contains(code)) is { } unknown
                ? DayEntries.UnknownSymptom(unknown)
                : null
        };

    private static DateOnly? Parse(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}