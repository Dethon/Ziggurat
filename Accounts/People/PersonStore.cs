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

    // What the session check and every app asking who is signed in read on their next request; a chat
    // hub connection already open keeps the name it learned as it arrived.
    public async Task<bool> SetProfileAsync(string userId, Profile profile, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.DisplayName, profile.DisplayName)
                .SetProperty(x => x.AvatarUrl, profile.AvatarUrl), ct) > 0;
    }
}