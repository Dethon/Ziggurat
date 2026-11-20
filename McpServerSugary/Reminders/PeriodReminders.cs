using Domain.DTOs.Channel;
using McpServerSugary.Glucose;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Reminders;

// Tells her phones before her period, if she turned it on: from DaysBefore days ahead of the start her
// cycles make likeliest (CycleForecast, the app's forecast to the day) until the day before it, from the
// reminder hour on her household's clock, once per expected start. On or past that day nothing is
// sent: she may be on it. Only somebody who turned it on (it shows on a lock screen), owns the row
// (never legacy) and has Sugary on is told, on her own phones; a reminder no phone took waits for one.
//
// The one reader of every person's cycle at once, so it opens the factory's context, which owns
// nothing, and asks for the whole tables in so many words.
public sealed class PeriodReminders(
    IDbContextFactory<SugaryDbContext> contexts,
    HomeCalendar calendar,
    ReminderPhones tell,
    SugarySettings settings,
    SugaryOff sugaryOff)
{
    public const int DaysBefore = 2;

    private readonly SemaphoreSlim _pass = new(1, 1);

    public async Task RemindAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            var off = await sugaryOff.OffAsync(ct);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var phones = await ReminderPhones.OfEveryoneAsync(db, ct);
            var wanting = (await db.CycleReminders.IgnoreQueryFilters()
                    .Where(r => r.Enabled && r.UserId != Owners.Unassigned)
                    .ToListAsync(ct))
                .Where(r => !off.PersonOff(new Account(r.TenantId, r.UserId)))
                .ToList();
            var owners = wanting.Select(r => r.UserId).ToHashSet();
            var periods = (await db.Periods.IgnoreQueryFilters().Where(p => owners.Contains(p.UserId)).ToListAsync(ct))
                .ToLookup(p => (p.TenantId, p.UserId), p => new CyclePeriod(p.Start, p.End));

            foreach (var reminder in wanting)
            {
                var owner = (reminder.TenantId, reminder.UserId);
                var clock = ReminderPhones.ClockOf(calendar, phones[owner]);
                if (clock.AtHome(clock.Now).Hour < settings.PenReminderHour
                    || CycleForecast.Of([.. periods[owner]], clock.Today) is not { } expected)
                {
                    continue;
                }

                var daysTo = expected.Likeliest.DayNumber - clock.Today.DayNumber;
                if (daysTo is < 1 or > DaysBefore || reminder.SentFor == expected.Likeliest)
                {
                    continue;
                }

                if (await tell.TellAsync(db, phones[owner], phone => PeriodReminderText.For(expected.Likeliest, daysTo, phone.Language), ct))
                {
                    reminder.SentFor = expected.Likeliest;
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