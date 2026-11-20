using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record CycleReminderDto(bool On);

// Whether her phones are told before her period (PeriodReminders). Off until she turns it on.
public static class CycleReminderEndpoints
{
    public static void MapCycleReminderEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/cycle/reminder", GetAsync);
        api.MapPut("/cycle/reminder", SetAsync);
    }

    private static async Task<CycleReminderDto> GetAsync(SugaryDbContext db, CancellationToken ct) =>
        new(await db.CycleReminders.AsNoTracking().AnyAsync(r => r.Enabled, ct));

    private static async Task<IResult> SetAsync(CycleReminderDto request, SugaryDbContext db, CancellationToken ct)
    {
        var reminder = await db.CycleReminders.FirstOrDefaultAsync(ct);
        if (reminder is null)
        {
            reminder = new CycleReminder();
            db.CycleReminders.Add(reminder);
        }

        reminder.Enabled = request.On;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CycleReminderDto(reminder.Enabled));
    }
}