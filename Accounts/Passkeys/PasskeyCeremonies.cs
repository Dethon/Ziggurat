using System.Text;
using Accounts.Links;
using Accounts.Sessions;
using Accounts.Store;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Passkeys;

// A ceremony's first half: an id to finish it by, and the options the browser hands its
// authenticator, as the JSON WebAuthn's own encoding gives them.
public sealed record BegunCeremony(string Id, string OptionsJson);

// The two WebAuthn ceremonies. Each begins with options held server-side and finishes by checking
// the authenticator's answer against them, and a finished one is a session.
public sealed class PasskeyCeremonies(
    IFido2 fido2,
    IDbContextFactory<AccountsDbContext> contexts,
    LinkStore links,
    SessionStore sessions,
    CeremonyStore ceremonies,
    AccountsClock clock)
{
    private sealed record Enrollment(string LinkId, string UserId, CredentialCreateOptions Options);

    private sealed record SignIn(AssertionOptions Options);

    // A discoverable credential, so that signing in asks for no username: the authenticator keeps
    // the account and offers it. User verification is preferred rather than required, so a key with
    // no PIN still works.
    public async Task<BegunCeremony?> BeginEnrollmentAsync(string token, CancellationToken ct)
    {
        if (await links.FindOpenAsync(token, ct) is not { } link)
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var held = await db.Credentials
            .Where(c => c.UserId == link.UserId && c.RevokedAt == null)
            .Select(c => c.CredentialId)
            .ToListAsync(ct);
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            // The handle the authenticator stores and hands back at sign-in. The user id is opaque
            // already, so it carries nothing a person would not want an authenticator to keep.
            User = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(link.UserId), Name = link.DisplayName, DisplayName = link.DisplayName
            },
            // A second passkey on the same authenticator would only replace the first.
            ExcludeCredentials = [.. held.Select(id => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(id)))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        return new BegunCeremony(ceremonies.Hold(new Enrollment(link.LinkId, link.UserId, options)), options.ToJson());
    }

    public async Task<IssuedSession?> FinishEnrollmentAsync(
        string ceremonyId, AuthenticatorAttestationRawResponse answer, string? device, CancellationToken ct)
    {
        if (ceremonies.Take<Enrollment>(ceremonyId) is not { } enrollment)
        {
            return null;
        }

        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = answer,
                OriginalOptions = enrollment.Options,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                {
                    var id = WebEncoders.Base64UrlEncode(args.CredentialId);
                    await using var check = await contexts.CreateDbContextAsync(token);
                    return !await check.Credentials.AnyAsync(c => c.CredentialId == id, token);
                }
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await links.SpendAsync(db, enrollment.LinkId, ct))
        {
            return null;
        }

        var user = await db.Users.SingleAsync(u => u.Id == enrollment.UserId, ct);
        var credential = new Credential
        {
            Id = Tokens.NewId(),
            TenantId = user.TenantId,
            UserId = user.Id,
            CredentialId = WebEncoders.Base64UrlEncode(registered.Id),
            PublicKey = registered.PublicKey,
            SignCount = registered.SignCount,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await sessions.IssueAsync(user.Id, credential.Id, device, ct);
    }

    // No allowed credentials: the authenticator offers whichever of its discoverable ones belong to
    // this relying party, and the answer's user handle says whose it is.
    public BegunCeremony BeginSignIn()
    {
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            UserVerification = UserVerificationRequirement.Preferred
        });
        return new BegunCeremony(ceremonies.Hold(new SignIn(options)), options.ToJson());
    }

    public async Task<IssuedSession?> FinishSignInAsync(
        string ceremonyId, AuthenticatorAssertionRawResponse answer, string? device, CancellationToken ct)
    {
        if (ceremonies.Take<SignIn>(ceremonyId) is not { } signIn)
        {
            return null;
        }

        var credentialId = WebEncoders.Base64UrlEncode(answer.RawId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Credentials.FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.RevokedAt == null, ct)
            is not { } credential)
        {
            return null;
        }

        VerifyAssertionResult verified;
        try
        {
            verified = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = answer,
                OriginalOptions = signIn.Options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(Encoding.UTF8.GetString(args.UserHandle) == credential.UserId)
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return null;
        }

        credential.SignCount = verified.SignCount;
        credential.LastUsedAt = clock.Time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await sessions.IssueAsync(credential.UserId, credential.Id, device, ct);
    }
}