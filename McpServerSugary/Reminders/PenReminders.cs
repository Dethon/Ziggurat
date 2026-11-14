using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Push;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Reminders;

// Tells each open pen that is due to its owner's subscribed phones, once per reminder. A pen is one
// person's and so is a phone that subscribed, so a reminder reaches nobody else in the household;
// a pen nobody owns yet (from before the log was per person) reaches nobody. A reminder no phone
// accepted is not marked sent, so it waits for a phone rather than being spent on none.
//
// The one reader of every person's pens at once, so it opens the factory's context, which owns
// nothing, and asks for the whole tables in so many words.
public sealed class PenReminders(
    IDbContextFactory<SugaryDbContext> contexts,
    HomeCalendar calendar,
    IPushMessageSender push,
    SugarySettings settings,
    ILogger<PenReminders> logger,
    ISessionAccounts? sessions = null)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // The loop and a test may ask at once; one pass at a time keeps each reminder single.
    private readonly SemaphoreSlim _pass = new(1, 1);

    public async Task RemindAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var phones = (await db.PushSubscriptions.IgnoreQueryFilters().ToListAsync(ct))
                .ToLookup(phone => (phone.TenantId, phone.UserId));
            // Each owner's day and hour on their household's clock, as their phone last told it.
            var due = (await db.Pens.IgnoreQueryFilters()
                    .Where(p => p.ReplacedOn == null && p.UserId != Owners.Unassigned)
                    .ToListAsync(ct))
                .Select(pen => (Pen: pen, Clock: ClockOf(phones[(pen.TenantId, pen.UserId)])))
                .Where(p => p.Clock.AtHome(p.Clock.Now).Hour >= settings.PenReminderHour)
                .Select(p => (p.Pen, p.Clock.Today, Reminder: PenRules.Due(p.Pen, p.Clock.Today)))
                .Where(d => d.Reminder is not null)
                .ToList();

            foreach (var (pen, today, reminder) in due)
            {
                var heard = await SendAsync(db, [.. phones[(pen.TenantId, pen.UserId)]], pen, reminder!.Value, today, ct);
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

    // A phone hears only while the session that subscribed it lives: one that ended is removed; one
    // that could not be asked about just now is told nothing this time and kept.
    private async Task<bool> SessionLivesAsync(SugaryDbContext db, PushSubscription phone, CancellationToken ct)
    {
        if (sessions is null || phone.SessionId is not { } session)
        {
            return true;
        }

        try
        {
            if (await sessions.SessionAsync(phone.UserId, session, ct) is not null)
            {
                return true;
            }

            db.PushSubscriptions.Remove(phone);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not ask whether the session behind a phone of {UserId} lives; not told", phone.UserId);
            return false;
        }
    }

    // The zone a person's phones last saved, else the server's own: a zone this host does not know
    // falls back to it too, as a request's does.
    private HomeCalendar ClockOf(IEnumerable<PushSubscription> phones) =>
        phones.Select(p => p.TimeZone).OfType<string>()
            .Select(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null)
            .OfType<TimeZoneInfo>()
            .FirstOrDefault() is { } zone
            ? calendar.In(zone)
            : calendar;

    private async Task<bool> SendAsync(
        SugaryDbContext db, IReadOnlyList<PushSubscription> phones, Pen pen, PenReminder reminder, DateOnly today,
        CancellationToken ct)
    {
        var heard = false;
        foreach (var phone in phones.Where(p => db.Entry(p).State != EntityState.Deleted))
        {
            if (!await SessionLivesAsync(db, phone, ct))
            {
                continue;
            }

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