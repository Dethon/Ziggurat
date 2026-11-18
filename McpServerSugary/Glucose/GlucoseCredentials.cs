using System.Text;
using System.Text.Json;
using Domain.DTOs.Channel;
using Domain.Security.Sealing;

namespace McpServerSugary.Glucose;

// A linked LibreLinkUp account's password, sealed for the person who linked it: the associated data
// is her household's and her own id, so a sealed password copied onto anyone else's link does not
// open. The key is the deployment's (SealingKey, SUGARY_SEALING_KEY); without one nothing is sealed,
// so nobody links, and a link kept from before opens to nothing until the key is back.
public sealed class GlucoseCredentials(SealingKey? key)
{
    public bool CanSeal => key is not null;

    public byte[] Seal(Account owner, string password)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(password);
        return (key ?? throw new InvalidOperationException("No sealing key is configured (SealingKey)."))
            .Seal(Encoding.UTF8.GetBytes(password), AssociatedData(owner));
    }

    // Null when there is no key, the key is another, or the password was sealed for someone else: the
    // tag cannot tell those apart, and none of them is a password to log in with.
    public string? Open(Account owner, byte[] sealedPassword)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (key is null)
        {
            return null;
        }

        var opened = key.Open(sealedPassword, AssociatedData(owner));
        return opened.IsOpened ? Encoding.UTF8.GetString(opened.Value) : null;
    }

    // A JSON array rather than ids joined by a separator, so no two owners spell the same bytes however
    // their ids are written.
    private static byte[] AssociatedData(Account owner) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { "sugary-glucose-link", owner.TenantId, owner.UserId });
}