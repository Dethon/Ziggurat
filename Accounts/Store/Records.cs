namespace Accounts.Store;

// A household. Only the home one exists in this phase, but every record below carries its tenant
// so that adding a second is data, not a schema change.
public sealed class Tenant
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    // The operator's own household, seeded at first start: the one the home-only features (vault,
    // satellites, the printer) belong to.
    public bool IsHome { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public enum Role
{
    Owner,
    Member
}

// A person. Their channel identities (a numeric Telegram id, a voice profile) arrive as rows of
// their own that point here, so this table never grows a column per channel.
public sealed class User
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public Role Role { get; set; }

    // Runs the deployment: their own recovery is the container command, and the dashboard is theirs.
    public bool IsOperator { get; set; }

    public bool SeesAllConversations { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

// A passkey. Revoked rather than deleted, so a session can still say which passkey began it.
public sealed class Credential
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }

    // What the authenticator calls it, base64url: how an assertion names the passkey it used.
    public required string CredentialId { get; init; }

    public required byte[] PublicKey { get; init; }
    public long SignCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum LinkKind
{
    // Creates a person, with the display name and role it names, when it is redeemed.
    Invite,

    // Adds a passkey to a person who already exists: a member who lost every device, and the
    // operator's own first enrollment, which is a recovery of an account with no passkey yet.
    Recovery
}

// An invite or recovery link. Only a hash of its token is kept, so the store alone opens nothing.
public sealed class Link
{
    public required string Id { get; init; }
    public required string TokenHash { get; init; }
    public LinkKind Kind { get; init; }
    public required string TenantId { get; init; }
    public string? UserId { get; init; }
    public string? DisplayName { get; init; }
    public Role? Role { get; init; }

    // Who minted it; none for the operator's command, which runs as nobody.
    public string? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
}

// A signed-in browser. Only a hash of the cookie's value is kept, so the store alone opens nothing.
public sealed class Session
{
    public required string Id { get; init; }
    public required string TokenHash { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }

    // The passkey that began it, so revoking the passkey ends it; none for a session the operator's
    // command minted.
    public string? CredentialId { get; init; }

    public string? Device { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}