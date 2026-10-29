using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.People;

public sealed class PersonStore(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    // A person of the home household with no passkey yet: a link enrolls one, a session can be
    // minted for them, and nothing else about them is decided here.
    public async Task<User> CreateAsync(string displayName, Role role, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("a person needs a display name", nameof(displayName));
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var home = await db.Tenants.SingleAsync(t => t.IsHome, ct);
        var person = new User
        {
            Id = Tokens.NewId(),
            TenantId = home.Id,
            DisplayName = displayName.Trim(),
            Role = role,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.Users.Add(person);
        await db.SaveChangesAsync(ct);
        return person;
    }
}