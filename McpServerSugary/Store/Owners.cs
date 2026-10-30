namespace McpServerSugary.Store;

// A row of one person's log: a meal, a dose, a pen, a period, a day's note, a session of exercise, a
// phone that asked for reminders. Both ids are the accounts service's, the household's beside the
// person's because every record carries its tenant.
public interface IPersonal
{
    string UserId { get; set; }
    string TenantId { get; set; }
}

// A row of the household's kitchen, shared by everyone in it: a food, a recipe.
public interface IHousehold
{
    string TenantId { get; set; }
}

public static class Owners
{
    // Whose the rows written before the log was per person are, in every owner column, until the
    // migration into the home household names the person they belong to. An account's id is 32
    // lowercase hex characters, so no request's person can ever be this one: the rows are nobody's
    // until assigned, seen by no request, no turn and no reminder.
    public const string Unassigned = "legacy";
}