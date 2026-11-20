using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Push;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Reminders;

// Her phones, and telling them: what every reminder that wakes for no request shares (pens, periods).
// A phone is one person's, so a reminder reaches its owner's alone. A phone hears only while the session
// that subscribed it lives: one that ended is removed; one that could not be asked about just now is
// told nothing this time and kept. One the push service says is gone is forgotten.
public sealed class ReminderPhones(IPushMessageSender push, ILogger<ReminderPhones> logger, ISessionAccounts? sessions = null)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // Every person's phones, by owner: the factory's context owns nothing, so the table is asked for whole.
    public static async Task<ILookup<(string TenantId, string UserId), PushSubscription>> OfEveryoneAsync(
        SugaryDbContext db, CancellationToken ct) =>
        (await db.PushSubscriptions.IgnoreQueryFilters().ToListAsync(ct)).ToLookup(phone => (phone.TenantId, phone.UserId));

    // Her day and hour on her household's clock, as her phones last told it.
    public static HomeCalendar ClockOf(HomeCalendar calendar, IEnumerable<PushSubscription> phones) =>
        calendar.OnSaved(phones.Select(p => p.TimeZone));

    // Whether any phone took it: a reminder nobody heard is not spent.
    public async Task<bool> TellAsync(
        SugaryDbContext db, IEnumerable<PushSubscription> phones, Func<PushSubscription, object> notification,
        CancellationToken ct)
    {
        var heard = false;
        foreach (var phone in phones.Where(p => db.Entry(p).State != EntityState.Deleted))
        {
            if (!await SessionLivesAsync(db, phone, ct))
            {
                continue;
            }

            var payload = JsonSerializer.Serialize(notification(phone), _json);
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
                logger.LogWarning(ex, "Pushing a reminder to {Endpoint} failed", phone.Endpoint);
            }
        }

        return heard;
    }

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
}