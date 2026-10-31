using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// A schedule runs as the principal of the turn that last wrote it, and a fire acts for that
// principal's account (.claude/rules/scheduling.md). One written before accounts records a channel
// and the name the channel knew its writer by, and no account, so it would now run as nobody. Its
// writer is found by that name and their account stamped beside it; the chat client's and voice's
// own user id is the account's from now on, so theirs becomes it too. One whose principal names
// nobody — the voice guest, a stranger, a schedule from before principals were recorded — runs as the
// owner, as if they had written it in their chat client: the spec's answer for a schedule nobody can
// be told about.
//
// Every schedule from before accounts is also from before households, so it is read where it still
// is, under the shape ScheduleHouseholds then moves into the home household (`schedule:{id}`, the
// `schedules` set), and stamped in place; a household's schedule is never touched.
public sealed class ScheduleCreators(IConnectionMultiplexer redis) : IMigrationStep
{
    private static readonly string[] _channelsKeyedByAccount = [ChannelIds.SignalR, ChannelIds.Voice];

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Schedules");
        var db = redis.GetDatabase();
        var stamped = (await ScheduleHouseholds.UnmovedAsync(db))
            .Where(s => s.CreatedBy?.Account is null)
            .Select(s => (Schedule: s, Writer: household.Named(s.CreatedBy?.UserId)))
            .ToList();
        foreach (var (schedule, writer) in dryRun ? [] : stamped)
        {
            // Its own key, its own expiry: a one-shot still expires an hour past its time, and one that
            // fired or was deleted meanwhile is not brought back.
            await db.StringSetAsync(
                ScheduleHouseholds.UnmovedKey(schedule.Id),
                JsonSerializer.Serialize(schedule with { CreatedBy = Stamp(schedule.CreatedBy, writer ?? household.Owner) }),
                expiry: null, keepTtl: true, when: When.Exists);
        }

        report.Changes = stamped.Count;
        report.Lines.AddRange(stamped
            .GroupBy(s => s.Writer ?? household.Owner)
            .Select(g => $"{g.Key.Label}: {Counted.Of(g.Count(), "schedule")}"
                         + (g.Any(s => s.Writer is null) ? $", {g.Count(s => s.Writer is null)} of them written by nobody it knows" : "")));
        return report;
    }

    private static Principal Stamp(Principal? old, MigratedPerson runsAs) =>
        old is not null && runsAs.Answers(old.UserId)
            ? new Principal(
                old.ChannelId,
                _channelsKeyedByAccount.Contains(old.ChannelId) ? runsAs.UserId! : old.UserId,
                runsAs.Account)
            : new Principal(ChannelIds.SignalR, runsAs.UserId!, runsAs.Account);
}