using Infrastructure.Memory;

namespace HouseholdMigration;

// Where the stores the migration moves data in are, as the stack's compose service hands them over:
// Redis and the accounts service by address, the Sugary log and the voice profiles as mounted
// volumes. The secrets are the stack's own (REDISCONNECTIONSTRING's password, LOGINS__STATEKEY) and
// the one only this run holds (ACCOUNTS__CREDENTIAL, CALLERS__household-migration at the other end).
public record MigrationSettings
{
    public string RedisConnectionString { get; init; } = "";

    public AccountsEndpoint Accounts { get; init; } = new();

    public LoginsSettings Logins { get; init; } = new();

    // The Sugary server's own database, in its volume. No file there is no log to give away.
    public string SugaryDatabasePath { get; init; } = "/sugary/sugary.db";

    // The voice channel's /voices, a folder per enrolled speaker.
    public string VoicesPath { get; init; } = "/voices";

    // The memory index's width, used only if the run is the one to create the index: the agent made
    // it long before, at the width its embeddings have, and a run that made it at another width would
    // leave every recall failing against it.
    public int MemoryDimension { get; init; } = EmbeddingOptions.DefaultDimension;
}

public record AccountsEndpoint
{
    public string BaseUrl { get; init; } = "http://accounts:8080";

    // What the accounts service admits the migration by, and nobody else: the same value as its
    // CALLERS__household-migration.
    public string Credential { get; init; } = "";
}

public record LoginsSettings
{
    // The browse server's key, base64 of 32 bytes: the old records are opened and the new ones sealed
    // with it. Empty with records to move is reported, and they are left where they are.
    public string StateKey { get; init; } = "";
}