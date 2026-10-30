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

// The WebAuthn ceremonies: enrolling through a link, adding a passkey while signed in, and signing
// in. Each begins with options held server-side and finishes by checking the authenticator's answer
// against them; a finished enrollment or sign-in is a session.
public sealed class PasskeyCeremonies(
    IFido2 fido2,
    IDbContextFactory<AccountsDbContext> contexts,
    LinkStore links,
    SessionStore sessions,
    CeremonyStore ceremonies,
    AccountsClock clock)
{
    // A link's enrollment. An invite's person does not exist until the ceremony finishes, but the
    // authenticator stores their id as the passkey's user handle at its start, so the id is chosen
    // then and the person is created with it.
    private sealed record Enrollment(string LinkId, LinkKind Kind, string UserId, CredentialCreateOptions Options);

    // A signed-in person adding another passkey from their account page.
    private sealed record Addition(string UserId, CredentialCreateOptions Options);

    private sealed record SignIn(AssertionOptions Options);

    public async Task<BegunCeremony?> BeginEnrollmentAsync(string token, CancellationToken ct)
    {
        if (await links.FindOpenAsync(token, ct) is not { } link)
        {
            return null;
        }

        var userId = link.UserId ?? Tokens.NewId();
        var options = await NewCredentialOptionsAsync(userId, link.DisplayName, ct);
        return new BegunCeremony(ceremonies.Hold(new Enrollment(link.LinkId, link.Kind, userId, options)), options.ToJson());
    }

    public async Task<IssuedSession?> FinishEnrollmentAsync(
        string ceremonyId, AuthenticatorAttestationRawResponse answer, string? device, CancellationToken ct)
    {
        if (ceremonies.Take<Enrollment>(ceremonyId) is not { } enrollment
            || await RegisterAsync(enrollment.Options, answer, ct) is not { } registered)
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = enrollment.Kind == LinkKind.Invite
            ? await links.RedeemInviteAsync(db, enrollment.LinkId, enrollment.UserId, ct)
            : await links.SpendAsync(db, enrollment.LinkId, ct)
                ? await db.Users.SingleAsync(u => u.Id == enrollment.UserId, ct)
                : null;
        if (user is null)
        {
            return null;
        }

        var credential = await AddCredentialAsync(db, user, registered, ct);
        await transaction.CommitAsync(ct);
        return await sessions.IssueAsync(user.Id, credential.Id, device, ct);
    }

    public async Task<BegunCeremony?> BeginAdditionAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) is not { } user)
        {
            return null;
        }

        var options = await NewCredentialOptionsAsync(user.Id, user.DisplayName, ct);
        return new BegunCeremony(ceremonies.Hold(new Addition(user.Id, options)), options.ToJson());
    }

    // No new session: the browser adding the passkey is signed in already, with the one it has.
    public async Task<bool> FinishAdditionAsync(
        string userId, string ceremonyId, AuthenticatorAttestationRawResponse answer, CancellationToken ct)
    {
        if (ceremonies.Take<Addition>(ceremonyId) is not { } addition
            || addition.UserId != userId
            || await RegisterAsync(addition.Options, answer, ct) is not { } registered)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) is not { } user)
        {
            return false;
        }

        await AddCredentialAsync(db, user, registered, ct);
        return true;
    }

    // A discoverable credential, so that signing in asks for no username: the authenticator keeps
    // the account and offers it. User verification is preferred rather than required, so a key with
    // no PIN still works.
    private async Task<CredentialCreateOptions> NewCredentialOptionsAsync(string userId, string displayName, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var held = await db.Credentials
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .Select(c => c.CredentialId)
            .ToListAsync(ct);
        return fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            // The handle the authenticator stores and hands back at sign-in. The user id is opaque
            // already, so it carries nothing a person would not want an authenticator to keep.
            User = new Fido2User { Id = Encoding.UTF8.GetBytes(userId), Name = displayName, DisplayName = displayName },
            // A second passkey on the same authenticator would only replace the first.
            ExcludeCredentials = [.. held.Select(id => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(id)))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
    }

    private async Task<RegisteredPublicKeyCredential?> RegisterAsync(
        CredentialCreateOptions options, AuthenticatorAttestationRawResponse answer, CancellationToken ct)
    {
        try
        {
            return await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = answer,
                OriginalOptions = options,
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
    }

    private async Task<Credential> AddCredentialAsync(
        AccountsDbContext db, User user, RegisteredPublicKeyCredential registered, CancellationToken ct)
    {
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
        return credential;
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