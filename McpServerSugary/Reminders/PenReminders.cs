using System.Net;
using System.Text.Json;
using Infrastructure.Clients.Push;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Reminders;

// Tells every subscribed phone about each open pen that is due, once per reminder. A reminder no
// phone accepted is not marked sent, so it waits for a phone rather than being spent on none.
public sealed class PenReminders(
    IDbContextFactory<SugaryDbContext> contexts,
    HomeCalendar calendar,
    IPushMessageSender push,
    SugarySettings settings,
    ILogger<PenReminders> logger)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // The loop and a test may ask at once; one pass at a time keeps each reminder single.
    private readonly SemaphoreSlim _pass = new(1, 1);

    public async Task RemindAsync(CancellationToken ct)
    {
        if (calendar.AtHome(calendar.Now).Hour < settings.PenReminderHour)
        {
            return;
        }

        await _pass.WaitAsync(ct);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var today = calendar.Today;
            var due = (await db.Pens.Where(p => p.ReplacedOn == null).ToListAsync(ct))
                .Select(pen => (Pen: pen, Reminder: PenRules.Due(pen, today)))
                .Where(d => d.Reminder is not null)
                .ToList();
            if (due.Count == 0)
            {
                return;
            }

            var phones = await db.PushSubscriptions.ToListAsync(ct);
            foreach (var (pen, reminder) in due)
            {
                var heard = await SendAsync(db, phones, pen, reminder!.Value, today, ct);
                if (heard)
                {
                    pen.Sent(reminder.Value);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<bool> SendAsync(
        SugaryDbContext db, IReadOnlyList<PushSubscription> phones, Pen pen, PenReminder reminder, DateOnly today,
        CancellationToken ct)
    {
        var heard = false;
        foreach (var phone in phones.Where(p => db.Entry(p).State != EntityState.Deleted))
        {
            var payload = JsonSerializer.Serialize(PenReminderText.For(pen, reminder, today, phone.Language), _json);
            try
            {
                await push.SendAsync(phone.Endpoint, phone.P256dh, phone.Auth, payload, ct);
                heard = true;
            }
            catch (WebPushSendException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                db.PushSubscriptions.Remove(phone);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Pushing a pen reminder to {Endpoint} failed", phone.Endpoint);
            }
        }

        return heard;
    }
}