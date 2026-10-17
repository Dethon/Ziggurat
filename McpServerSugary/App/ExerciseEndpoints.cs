using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record ExerciseDto(
    Guid Id, ExerciseKind Kind, string? Name, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Note);

// Kind is text rather than the enum so a kind the app does not know is refused with a sentence,
// where a failed bind would be a bare 400; the start is nullable so a missing one is refused rather
// than stored as the first day of the calendar. No end is a session still running.
public record SaveExerciseRequest(
    string? Kind, string? Name, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Note);

// Her exercise, recorded by her as she does it. Starting a session is creating one with no end, and
// stopping it is sending it again with one.
public static class ExerciseEndpoints
{
    private const int MaxNames = 8;

    public static void MapExerciseEndpoints(this IEndpointRouteBuilder api)
    {
        var exercise = api.MapGroup("/exercise");
        exercise.MapGet("/", ListAsync);
        exercise.MapGet("/names", NamesAsync);
        exercise.MapPost("/", CreateAsync);
        exercise.MapPut("/{id:guid}", ReplaceAsync);
        exercise.MapDelete("/{id:guid}", DeleteAsync);
    }

    // Newest start first and paged, as doses are, and bounded to [from, to) by the start when the
    // Log asks for the days it shows. A running session is among them, with no end.
    private static async Task<IReadOnlyList<ExerciseDto>> ListAsync(
        SugaryDbContext db, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        int skip = 0, int take = 50)
    {
        var lower = from ?? DateTimeOffset.MinValue;
        var upper = to ?? DateTimeOffset.MaxValue;
        var rows = await db.ExerciseSessions.AsNoTracking()
            .Where(e => e.StartedAt >= lower && e.StartedAt < upper)
            .OrderByDescending(e => e.StartedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    // What she called her other sessions, each once and the latest first, for Otro to offer back:
    // the page of sessions the Log holds forgets a name once its sessions scroll off.
    private static async Task<IReadOnlyList<string>> NamesAsync(SugaryDbContext db, CancellationToken ct)
    {
        var named = await db.ExerciseSessions.AsNoTracking()
            .Where(e => e.Kind == ExerciseKind.Other && e.Name != null)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => e.Name!)
            .ToListAsync(ct);
        return [.. named.Distinct().Take(MaxNames)];
    }

    private static async Task<IResult> CreateAsync(
        SaveExerciseRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await ValidateAsync(request, null, db, calendar, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var session = new ExerciseSession { Id = Guid.NewGuid() };
        Apply(request, session);
        db.ExerciseSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/exercise/{session.Id}", ToDto(session));
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id, SaveExerciseRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await db.ExerciseSessions.FindAsync([id], ct) is not { } session)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(request, id, db, calendar, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        Apply(request, session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(session));
    }

    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.ExerciseSessions.Where(e => e.Id == id).ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent()
            : Results.NotFound();

    private static void Apply(SaveExerciseRequest request, ExerciseSession session)
    {
        var kind = ExerciseRules.Kind(request.Kind)!.Value;
        session.Kind = kind;
        session.Name = ExerciseRules.Name(kind, request.Name);
        session.StartedAt = request.StartedAt!.Value;
        session.EndedAt = request.EndedAt;
        session.Note = ExerciseRules.Note(request.Note);
    }

    private static async Task<string?> ValidateAsync(
        SaveExerciseRequest request, Guid? self, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (ExerciseRules.Problem(ExerciseRules.Kind(request.Kind), request.Name, request.StartedAt, request.EndedAt,
                request.Note, calendar.Now) is { } problem)
        {
            return problem;
        }

        if (request.EndedAt is not null)
        {
            return null;
        }

        var running = await db.ExerciseSessions.AsNoTracking()
            .Where(e => e.EndedAt == null && e.Id != self)
            .Select(e => (DateTimeOffset?)e.StartedAt)
            .FirstOrDefaultAsync(ct);
        return running is { } since ? ExerciseRules.Running(calendar.AtHome(since)) : null;
    }

    public static ExerciseDto ToDto(ExerciseSession session) =>
        new(session.Id, session.Kind, session.Name, session.StartedAt, session.EndedAt, session.Note);
}