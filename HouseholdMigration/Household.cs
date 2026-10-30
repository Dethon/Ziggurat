using Domain.DTOs.Channel;

namespace HouseholdMigration;

// A person of the file and the account they are now. A dry run makes nobody, so a person it would
// create has no user id yet, and every step says what it would move to "a new account".
public sealed record MigratedPerson(PersonEntry Entry, string? UserId, string TenantId, string DisplayName)
{
    public Account? Account => UserId is null ? null : new Account(TenantId, UserId);

    public string Label => UserId is null ? $"{DisplayName} (a new account)" : $"{DisplayName} (user {UserId})";

    // Whether something filed under this name is theirs: their account's own id, or a name they went by.
    // Names were matched without case by the channels that issued them, so they are here too.
    public bool Answers(string? name) =>
        name is not null
        && (string.Equals(UserId, name, StringComparison.Ordinal)
            || Entry.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
}

// The home household as the accounts service answered for the file: everyone in it, the owner who
// takes what was nobody's, and the person whose Sugary log it was.
public sealed record Household(
    string TenantId, IReadOnlyList<MigratedPerson> People, MigratedPerson Owner, MigratedPerson SugaryPerson)
{
    public MigratedPerson? Named(string? name) => People.FirstOrDefault(p => p.Answers(name));
}

// What one step did, or in a dry run would do: lines for the operator, how many things it changed —
// zero on a run after a complete one — and what it could not do and left where it was.
public sealed class StepReport(string title)
{
    public string Title => title;

    public List<string> Lines { get; } = [];

    public List<string> Problems { get; } = [];

    public int Changes { get; set; }
}

// "1 phone", "3 phones": how every report line counts what it moved.
public static class Counted
{
    public static string Of(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}

public interface IMigrationStep
{
    Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct);
}

public sealed class MigrationRefusedException(string message) : Exception(message);