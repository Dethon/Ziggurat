namespace Domain.DTOs;

// Where a household's schedules are kept in the deployment's Redis: `schedule@{tenantId}:{id}` and the
// household's set `schedules@{tenantId}`. Named once here because two services read them — the
// scheduler's store, and the accounts service, which keeps an owner from removing an agent that still
// runs some.
public static class ScheduleKeys
{
    public static string Schedule(string tenantId, string id) => $"schedule@{tenantId}:{id}";

    public const string HouseholdPrefix = "schedules@";

    public static string Household(string tenantId) => $"{HouseholdPrefix}{tenantId}";
}