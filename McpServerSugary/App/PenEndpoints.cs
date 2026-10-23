using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record PenDto(
    Guid Id, DoseKind Kind, string Insulin, DateOnly OpenedOn, int Days, DateOnly ReplaceOn, DateOnly? ReplacedOn);

// Kind is text and the rest nullable so a missing or unknown value is refused with a sentence rather
// than a bare 400 from a failed bind.
public record SavePenRequest(string? Kind, string? Insulin, DateOnly? OpenedOn, int? Days);

// Her insulin pens: one open pen of each kind, and the ones she replaced. When each is due is the
// pen's own arithmetic; the reminders are PenReminders'.
public static class PenEndpoints
{
    public const string UnknownKind = "A pen is rapid or basal.";
    public const string OpenedBeforeCurrent = "A new pen can't be first used before the one it replaces.";
    public const string OpenedAfterReplaced = "A pen can't be first used after the day it was replaced.";

    public static void MapPenEndpoints(this IEndpointRouteBuilder api)
    {
        var pens = api.MapGroup("/pens");
        pens.MapGet("/", ListAsync);
        pens.MapPost("/", CreateAsync);
        pens.MapPut("/{id:guid}", ReplaceAsync);
        pens.MapDelete("/{id:guid}", DeleteAsync);
    }

    // All of them, the newest first: a pen a month at most.
    private static async Task<IReadOnlyList<PenDto>> ListAsync(SugaryDbContext db, CancellationToken ct)
    {
        var rows = await db.Pens.AsNoTracking().ToListAsync(ct);
        return [.. rows.OrderByDescending(p => p.OpenedOn).ThenBy(p => p.ReplacedOn is not null).Select(ToDto)];
    }

    // A new pen of a kind replaces the open one, on the day the new one was first used.
    private static async Task<IResult> CreateAsync(
        SavePenRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (DoseRules.Kind(request.Kind) is not { } kind)
        {
            return Refuse(UnknownKind);
        }

        if (PenRules.Problem(request.Insulin, request.OpenedOn, request.Days, calendar.Today) is { } problem)
        {
            return Refuse(problem);
        }

        var openedOn = request.OpenedOn!.Value;
        var open = await db.Pens.Where(p => p.Kind == kind && p.ReplacedOn == null).ToListAsync(ct);
        if (open.Any(p => p.OpenedOn > openedOn))
        {
            return Refuse(OpenedBeforeCurrent);
        }

        open.ForEach(p => p.ReplacedOn = openedOn);
        var pen = new Pen
        {
            Id = Guid.NewGuid(), Kind = kind, Insulin = request.Insulin!.Trim(), OpenedOn = openedOn,
            Days = request.Days!.Value
        };
        db.Pens.Add(pen);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/pens/{pen.Id}", ToDto(pen));
    }

    // A correction of the insulin, the day or the days. The kind stays: a pen of the other kind is a
    // new pen.
    private static async Task<IResult> ReplaceAsync(
        Guid id, SavePenRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await db.Pens.FindAsync([id], ct) is not { } pen)
        {
            return Results.NotFound();
        }

        if (PenRules.Problem(request.Insulin, request.OpenedOn, request.Days, calendar.Today) is { } problem)
        {
            return Refuse(problem);
        }

        if (request.OpenedOn > pen.ReplacedOn)
        {
            return Refuse(OpenedAfterReplaced);
        }

        pen.Insulin = request.Insulin!.Trim();
        pen.OpenedOn = request.OpenedOn!.Value;
        pen.Days = request.Days!.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(pen));
    }

    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Pens.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static IResult Refuse(string problem) =>
        Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);

    private static PenDto ToDto(Pen pen) =>
        new(pen.Id, pen.Kind, pen.Insulin, pen.OpenedOn, pen.Days, pen.ReplaceOn, pen.ReplacedOn);
}