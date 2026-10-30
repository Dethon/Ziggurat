using Accounts.Links;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.People;

// One person of the retired identity map, as the household migration hands them over: the name the
// map knew them by (the key a second run finds them again by), the name they will go by, the numeric
// Telegram id the operator looked up for them, what they are in the household, and the picture the
// retired chat client's list showed beside them.
public sealed record ImportingPerson(
    string Key, string DisplayName, long? TelegramId, Role Role, bool Operator, string? AvatarUrl = null);

// What became of them. A person this run created gets a passkey link, minted once the import is
// committed; anybody else already has a way in, or is told the command that mints one. Changes say
// what this run did, so a second run that did nothing can say so.
public sealed record ImportedPerson(
    string Key,
    string? UserId,
    string DisplayName,
    Role Role,
    bool Operator,
    bool Created,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Notes,
    bool HasPasskey,
    MintedLink? Link);

public abstract record HouseholdImport
{
    public sealed record Imported(string TenantId, IReadOnlyList<ImportedPerson> People) : HouseholdImport;

    // Nothing was written: the request contradicts itself (Invalid) or the store (Conflict).
    public sealed record Invalid(string Reason) : HouseholdImport;

    public sealed record Conflict(string Reason) : HouseholdImport;
}

// Makes the identity map's people accounts of the home household, all at once or not at all. Each is
// found again by a linked identity of their own on the retired map's "channel", which no resolution
// answers, so the second run reuses whom the first made. The operator who already minted their own
// link, and anybody who already joined through Telegram, is adopted rather than made twice.
public sealed class HouseholdImporter(IDbContextFactory<AccountsDbContext> contexts, LinkStore links, AccountsClock clock)
{
    // The retired identity map, as a channel a person's old name there is an identity on.
    public const string Channel = "identity-map";

    public async Task<HouseholdImport> ImportAsync(
        IReadOnlyList<ImportingPerson> people, bool dryRun, CancellationToken ct)
    {
        if (Invalid(people) is { } reason)
        {
            return new HouseholdImport.Invalid(reason);
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var home = await db.Tenants.SingleAsync(t => t.IsHome, ct);
        var placed = new List<Placed>();

        // One context, so one person at a time: each reads what the ones before it decided.
        foreach (var person in people)
        {
            var (found, conflict) = await FindAsync(db, home, person, ct);
            if (found is null)
            {
                return conflict!;
            }

            if (placed.FirstOrDefault(p => p.User.Id == found.User.Id) is { } other)
            {
                return new HouseholdImport.Conflict(
                    $"{other.Asked.Key} and {person.Key} would both be {found.User.DisplayName}, who is one account");
            }

            if ((await PromoteAsync(db, home, found, ct) ?? await LinkTelegramAsync(db, found, ct)) is { } refused)
            {
                return refused;
            }

            GiveAvatar(found);

            placed.Add(found);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new HouseholdImport.Conflict("something else changed these people while they were imported; run it again");
        }

        var passkeys = await HoldersOfPasskeysAsync(db, placed.Select(p => p.User.Id).ToList(), ct);
        if (dryRun)
        {
            await transaction.RollbackAsync(ct);
            return new HouseholdImport.Imported(home.Id, [.. placed.Select(p => Result(p, passkeys, link: null, dryRun))]);
        }

        await transaction.CommitAsync(ct);
        var result = new List<ImportedPerson>();
        foreach (var p in placed)
        {
            var link = p.Created ? await links.MintRecoveryAsync(p.User.Id, ct) : null;
            result.Add(Result(p, passkeys, link, dryRun));
        }

        return new HouseholdImport.Imported(home.Id, result);
    }

    // A name and a picture are held to the account page's own rule (Profile): every app draws them the
    // same way, whoever set them.
    private static string? Invalid(IReadOnlyList<ImportingPerson> people) =>
        people.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.DisplayName))
            is not null
            ? "every person needs the name the identity map knew them by and a display name"
            : people.Select(p => (p.Key, Profile.Read(p.DisplayName, p.AvatarUrl).Problem))
                .FirstOrDefault(p => p.Problem is not null) is { Problem: { } problem } unfit
                ? $"{unfit.Key}: {problem}"
            : people.GroupBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice
                ? $"{twice.Key} is listed twice"
                : people.Where(p => p.TelegramId is not null).GroupBy(p => p.TelegramId).FirstOrDefault(g => g.Count() > 1)
                    is { } shared
                    ? $"Telegram {shared.Key} is listed for {string.Join(" and ", shared.Select(p => p.Key))}"
                    : people.Count(p => p.Operator) > 1
                        ? "only one person runs the deployment"
                        : null;

    // Whom this person already is, in order: the one an earlier import made, the operator who
    // minted their own link before any import, the one who joined through the Telegram listed for
    // them. Anybody else is made now, with no passkey yet.
    private async Task<(Placed? Found, HouseholdImport.Conflict? Conflict)> FindAsync(
        AccountsDbContext db, Tenant home, ImportingPerson person, CancellationToken ct)
    {
        if (await HolderAsync(db, Channel, person.Key, ct) is { } imported)
        {
            return (new Placed(person, imported, Created: false), null);
        }

        var adopted = person.Operator
            ? await db.Users.FirstOrDefaultAsync(u => u.IsOperator && u.TenantId == home.Id, ct)
            : null;
        adopted ??= person.TelegramId is { } telegramId
            ? await HolderAsync(db, TelegramLinks.Channel, TelegramLinks.IdentityOf(telegramId), ct)
            : null;
        if (adopted is not null)
        {
            var known = await db.LinkedIdentities
                .Where(i => i.UserId == adopted.Id && i.Channel == Channel)
                .Select(i => i.Identity)
                .FirstOrDefaultAsync(ct);
            if (known is not null)
            {
                return (null, new HouseholdImport.Conflict(
                    $"{adopted.DisplayName} is {known} of the identity map, not {person.Key}"));
            }

            AddIdentity(db, adopted, Channel, person.Key);
            var found = new Placed(person, adopted, Created: false);
            found.Changes.Add($"was already here as {adopted.DisplayName}, now {person.Key} of the identity map");
            return (found, null);
        }

        var created = new User
        {
            Id = Tokens.NewId(),
            TenantId = home.Id,
            DisplayName = person.DisplayName.Trim(),
            Role = person.Operator ? Role.Owner : person.Role,
            IsOperator = person.Operator,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.Users.Add(created);
        AddIdentity(db, created, Channel, person.Key);
        var made = new Placed(person, created, Created: true);
        made.Changes.Add("created");
        return (made, null);
    }

    // Named the operator or an owner, a person is made one; nobody is ever demoted, so a role an owner
    // changed since the last run is not undone.
    private static async Task<HouseholdImport?> PromoteAsync(
        AccountsDbContext db, Tenant home, Placed placed, CancellationToken ct)
    {
        var (person, user) = (placed.Asked, placed.User);
        if (person.Operator && !user.IsOperator)
        {
            if (await db.Users.FirstOrDefaultAsync(u => u.IsOperator && u.TenantId == home.Id && u.Id != user.Id, ct)
                is { } other)
            {
                return new HouseholdImport.Conflict($"the operator is {other.DisplayName} already, not {person.Key}");
            }

            user.IsOperator = true;
            placed.Changes.Add("made the operator");
        }

        if ((person.Operator || person.Role == Role.Owner) && user.Role != Role.Owner)
        {
            user.Role = Role.Owner;
            placed.Changes.Add("made an owner");
        }

        return null;
    }

    private async Task<HouseholdImport?> LinkTelegramAsync(AccountsDbContext db, Placed placed, CancellationToken ct)
    {
        var (person, user) = (placed.Asked, placed.User);
        if (person.TelegramId is not { } telegramId)
        {
            return null;
        }

        var identity = TelegramLinks.IdentityOf(telegramId);
        var holder = await HolderAsync(db, TelegramLinks.Channel, identity, ct);
        if (holder is not null)
        {
            return holder.Id == user.Id
                ? null
                : new HouseholdImport.Conflict($"Telegram {identity} is {holder.DisplayName}'s, not {person.Key}'s");
        }

        var theirs = await db.LinkedIdentities
            .Where(i => i.UserId == user.Id && i.Channel == TelegramLinks.Channel)
            .Select(i => i.Identity)
            .FirstOrDefaultAsync(ct);
        if (theirs is not null)
        {
            // They linked one of their own since; theirs is the one they use.
            placed.Notes.Add($"keeps Telegram {theirs} rather than {identity}");
            return null;
        }

        AddIdentity(db, user, TelegramLinks.Channel, identity);
        placed.Changes.Add($"linked to Telegram {identity}");
        return null;
    }

    // The picture the retired chat client showed, for an account that shows none yet; one its person
    // chose on the account page since is theirs and is kept, as an adopted account keeps its name.
    private static void GiveAvatar(Placed placed)
    {
        var (person, user) = (placed.Asked, placed.User);
        if (Profile.Read(person.DisplayName, person.AvatarUrl).Profile?.AvatarUrl is not { } avatar
            || avatar == user.AvatarUrl)
        {
            return;
        }

        if (user.AvatarUrl is not null)
        {
            placed.Notes.Add($"keeps the picture {user.AvatarUrl} rather than {avatar}");
            return;
        }

        user.AvatarUrl = avatar;
        placed.Changes.Add($"given the picture {avatar}");
    }

    private static Task<User?> HolderAsync(AccountsDbContext db, string channel, string identity, CancellationToken ct) =>
        db.LinkedIdentities
            .Where(i => i.Channel == channel && i.Identity == identity)
            .Join(db.Users, i => i.UserId, u => u.Id, (_, u) => u)
            .FirstOrDefaultAsync(ct);

    private void AddIdentity(AccountsDbContext db, User user, string channel, string identity) =>
        db.LinkedIdentities.Add(new LinkedIdentity
        {
            Id = Tokens.NewId(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Channel = channel,
            Identity = identity,
            CreatedAt = clock.Time.GetUtcNow()
        });

    private static async Task<HashSet<string>> HoldersOfPasskeysAsync(
        AccountsDbContext db, IReadOnlyList<string> userIds, CancellationToken ct) =>
        [
            .. await db.Credentials
                .Where(c => userIds.Contains(c.UserId) && c.RevokedAt == null)
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync(ct)
        ];

    private static ImportedPerson Result(Placed placed, HashSet<string> passkeys, MintedLink? link, bool dryRun) =>
        new(
            placed.Asked.Key,
            dryRun && placed.Created ? null : placed.User.Id,
            placed.User.DisplayName,
            placed.User.Role,
            placed.User.IsOperator,
            placed.Created,
            placed.Changes,
            placed.Notes,
            passkeys.Contains(placed.User.Id),
            link);

    private sealed record Placed(ImportingPerson Asked, User User, bool Created)
    {
        public List<string> Changes { get; } = [];
        public List<string> Notes { get; } = [];
    }
}