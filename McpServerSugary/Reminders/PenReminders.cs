using Domain.Contracts;
using Domain.DTOs.Channel;
using McpServerSugary.Glucose;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Reminders;

// Tells each open pen that is due to its owner's subscribed phones, once per reminder. A pen is one
// person's and so is a phone that subscribed, so a reminder reaches nobody else in the household;
// a pen nobody owns yet (from before the log was per person) reaches nobody, nor does the pen of a person
// an owner has not turned Sugary on for (SugaryOff) until it is on. A reminder no phone
// accepted is not marked sent, so it waits for a phone rather than being spent on none.
//
// The one reader of every person's pens at once, so it opens the factory's context, which owns
// nothing, and asks for the whole tables in so many words.
public sealed class PenReminders(
    IDbContextFactory<SugaryDbContext> contexts,
    HomeCalendar calendar,
    ReminderPhones tell,
    SugarySettings settings,
    SugaryOff sugaryOff)
{
    // The loop and a test may ask at once; one pass at a time keeps each reminder single.
    private readonly SemaphoreSlim _pass = new(1, 1);

    public async Task RemindAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            var off = await sugaryOff.OffAsync(ct);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var phones = await ReminderPhones.OfEveryoneAsync(db, ct);
            // Each owner's day and hour on their household's clock, as their phone last told it.
            var due = (await db.Pens.IgnoreQueryFilters()
                    .Where(p => p.ReplacedOn == null && p.UserId != Owners.Unassigned)
                    .ToListAsync(ct))
                .Where(pen => !off.PersonOff(new Account(pen.TenantId, pen.UserId)))
                .Select(pen => (Pen: pen, Clock: ReminderPhones.ClockOf(calendar, phones[(pen.TenantId, pen.UserId)])))
                .Where(p => p.Clock.AtHome(p.Clock.Now).Hour >= settings.PenReminderHour)
                .Select(p => (p.Pen, p.Clock.Today, Reminder: PenRules.Due(p.Pen, p.Clock.Today)))
                .Where(d => d.Reminder is not null)
                .ToList();

            foreach (var (pen, today, reminder) in due)
            {
                var heard = await tell.TellAsync(db, phones[(pen.TenantId, pen.UserId)],
                    phone => PenReminderText.For(pen, reminder!.Value, today, phone.Language), ct);
                if (heard)
                {
                    pen.Sent(reminder!.Value);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _pass.Release();
        }
    }
}