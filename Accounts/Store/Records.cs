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

    // When an owner disabled them; none while they are enabled. A disabled person is kept with
    // everything of theirs — passkeys, links, their Telegram, their voice profile, their memories and
    // topics — and none of it answers for them until an owner re-enables them.
    public DateTimeOffset? DisabledAt { get; set; }
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
    Recovery,

    // Links a person's Telegram to their account: the code from their account page, redeemed as the
    // bot's start parameter. It never opens the enrollment page.
    TelegramLink,

    // Binds a Telegram group to the household: an owner's code from the members page, sent to the bot
    // in the group as /bind. It never opens the enrollment page either.
    TelegramGroup
}

// A person's identity on a channel that is not a passkey: the numeric Telegram id the bot saw when
// they redeemed a code. A row of its own pointing at the person, so Users never grows a column per
// channel. A person holds at most one per channel, and an identity is one person's.
public sealed class LinkedIdentity
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string Channel { get; init; }

    // As the channel spells it: a Telegram id is its decimal digits.
    public required string Identity { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

// A group chat an owner's code bound to the household: its linked members' messages there are their
// own turns, and nobody's are in a group that is bound to no household.
public sealed class BoundChat
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Channel { get; init; }

    // As the channel spells it: a Telegram group's id is its (negative) decimal digits.
    public required string ChatId { get; init; }

    // The owner whose code bound it.
    public string? BoundBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

// An invite, recovery or Telegram link. Only a hash of its token is kept, so the store alone opens nothing.
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
// What one of a household's own servers opens its tunnel with, for one server name. Only a hash of
// the credential is kept, so the store alone opens no tunnel. Revoked rather than deleted, so a list
// still shows what was minted and when it stopped working.
public sealed class TunnelCredential
{
    public required string Id { get; init; }
    public required string CredentialHash { get; init; }
    public required string TenantId { get; init; }
    public required string ServerName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}
// One section of a household's configuration (Domain/DTOs/Household): a JSON document carrying its
// own schemaVersion, kept as plain text — not a provider's JSON column — with its secret fields
// sealed under the household's data key. A household holds at most one of each section; one it has
// not saved reads as the section's defaults.
public sealed class HouseholdSection
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Section { get; init; }
    public required string Document { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// The random key a household's secrets are sealed with, kept only wrapped by the deployment's master
// key. MasterKeyId says which master wrapped it, so a rotation can be rerun and a service started
// with the wrong master says so instead of failing to open every secret one at a time.
public sealed class HouseholdDataKey
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required byte[] WrappedKey { get; set; }
    public required string MasterKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RewrappedAt { get; set; }
}
// One change made through the admin API: who made it (the account the admin assertion named, with
// its own household, which for the operator is not always the one changed), the household changed,
// the route it came on and a summary its route wrote, which never carries a secret or any part of one,
// and when. Never updated or deleted.
public sealed class AdminChange
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string ActorUserId { get; init; }
    public required string ActorTenantId { get; init; }
    public required string Route { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset At { get; init; }
}
// What a provider last said of one of a household's keys: working, rejected (on saving, or in use),
// or unchecked (saved while the provider could not be asked). Kept beside the keys section rather
// than in it, because it is this service's observation, not the owner's configuration: a re-probe is
// not a save to announce to every reader, and the operator's get and set never carry it. Which key
// it was said of is its fingerprint (a SHA-256 of the key, as a session's token is kept), so a key
// replaced by the operator's command, which asks nobody, reads as unchecked rather than as its
// predecessor's state.
public sealed class HouseholdKeyCheck
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Key { get; init; }
    public required string Fingerprint { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string? Reason { get; set; }
}

// A name and custom instructions one of a household's agents had before an owner's change replaced
// them, kept so the owner can put them back: the admin API keeps the last few per agent and drops
// them with the agent. Not configuration — no turn reads it — so it is kept beside the agents
// section rather than in it.
public sealed class AgentVersion
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string? CustomInstructions { get; init; }
    public required string ReplacedBy { get; init; }
    public DateTimeOffset ReplacedAt { get; init; }
}