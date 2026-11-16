using System.Security.Cryptography;
using System.Text;
using Accounts.Settings;
using Accounts.Store;
using Domain.Security.Sealing;

namespace Accounts.Configuration;

// Where a household's secret is sealed for: the household, the section and the field, as one string,
// so a blob copied to another household, another section or another field does not open. The field
// is its path in the section's document (property names joined by `/`; a bot's token is under the id
// of the agent it is bound to). A household's data key is sealed the same way under the master key,
// for a section no section is named.
public static class HouseholdSealing
{
    public const string DataKeySection = "data-key";

    public static byte[] AssociatedData(string householdId, string section, string field) =>
        Encoding.UTF8.GetBytes($"ziggurat:household-config\n{householdId}\n{section}\n{field}");

    // Which master key wrapped a data key: the start of its SHA-256, which says nothing about the key.
    public static string MasterKeyId(SealingKey master) => Convert.ToHexStringLower(SHA256.HashData(master.Export()))[..16];
}

// Each household's data key, kept wrapped by the deployment's master key: generated the first time
// one of its secrets is sealed, unwrapped whenever one is opened. Rotating the master rewraps these
// and touches no sealed field (Rewrap).
public sealed class HouseholdDataKeys
{
    public const string MasterKeySetting = "HouseholdConfig:MasterKey";

    private readonly SealingKey? _master;

    // Parsed here, at registration, so a malformed key stops the service and every command before
    // anything is read rather than failing each secret in turn.
    public HouseholdDataKeys(AccountsSettings settings)
    {
        _master = SealingKey.Parse(settings.HouseholdConfig.MasterKey, MasterKeySetting);
        MasterKeyId = _master is null ? null : HouseholdSealing.MasterKeyId(_master);
    }

    public string? MasterKeyId { get; }

    public SealingKey Open(HouseholdDataKey row)
    {
        var master = Master();
        if (row.MasterKeyId != MasterKeyId)
        {
            throw new HouseholdSecretsUnavailableException(
                $"household {row.TenantId}'s data key is wrapped under master key {row.MasterKeyId}, and this service holds "
                + $"{MasterKeyId}: run rewrap-household-keys with the previous master key on its input");
        }

        return Unwrap(master, row) ?? throw new HouseholdSecretsUnavailableException(
            $"household {row.TenantId}'s data key does not open under the master key {MasterKeyId}");
    }

    public (HouseholdDataKey Row, SealingKey Key) Mint(string householdId, DateTimeOffset now)
    {
        var master = Master();
        var key = SealingKey.Generate();
        var row = new HouseholdDataKey
        {
            Id = Tokens.NewId(),
            TenantId = householdId,
            WrappedKey = Wrap(master, householdId, key),
            MasterKeyId = MasterKeyId!,
            CreatedAt = now
        };
        return (row, key);
    }

    // The row wrapped under this service's master instead of the previous one, or why not. A row
    // already under this master is checked to open and left as it is, so a rerun changes nothing.
    public string? Rewrap(HouseholdDataKey row, SealingKey previous, DateTimeOffset now)
    {
        var master = Master();
        if (row.MasterKeyId == MasterKeyId)
        {
            return Unwrap(master, row) is null ? $"household {row.TenantId}'s data key does not open under the master key" : null;
        }

        if (row.MasterKeyId != HouseholdSealing.MasterKeyId(previous) || Unwrap(previous, row) is not { } key)
        {
            return $"household {row.TenantId}'s data key is wrapped under master key {row.MasterKeyId}, which is neither "
                   + "the previous key given nor this service's";
        }

        row.WrappedKey = Wrap(master, row.TenantId, key);
        row.MasterKeyId = MasterKeyId!;
        row.RewrappedAt = now;
        return null;
    }

    private SealingKey Master() => _master ?? throw new HouseholdSecretsUnavailableException(
        $"no master key is configured ({MasterKeySetting}), so no household secret can be sealed or opened");

    private static byte[] Wrap(SealingKey master, string householdId, SealingKey key) =>
        master.Seal(key.Export(), HouseholdSealing.AssociatedData(householdId, HouseholdSealing.DataKeySection, ""));

    private static SealingKey? Unwrap(SealingKey master, HouseholdDataKey row) =>
        master.Open(row.WrappedKey, HouseholdSealing.AssociatedData(row.TenantId, HouseholdSealing.DataKeySection, "")) is
        { IsOpened: true } opened
            ? SealingKey.Import(opened.Value)
            : null;
}

public sealed class HouseholdSecretsUnavailableException(string message) : Exception(message);