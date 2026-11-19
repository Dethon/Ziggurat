---
paths:
  - "Accounts/Tunnels/**"
  - "Accounts/Internal/TunnelVerification.cs"
  - "Accounts/Internal/InternalCallers.cs"
  - "Accounts/Commands/AccountsCommand.cs"
  - "Accounts/Admin/HouseholdServersAdmin.cs"
  - "Accounts/Admin/HouseholdSuspensionAdmin.cs"
  - "Domain/DTOs/Tunnel/TunnelCredentials.cs"
  - "Infrastructure/Clients/Accounts/TunnelCredentialClient.cs"
  - "Tests/Integration/Accounts/TunnelCredentialsTests.cs"
---

# Tunnel credentials in the accounts service

See also: `accounts.md` (the service, its commands), `channel-identity.md` (the internal API's callers), `custom-agents.md`; the relay's side is `.claude/rules/tunnel.md`.

## Tunnel credentials

A household's own server opens its tunnel (`.scratch/tenants-tunnel/spec.md`) with a credential of its own: a `TunnelCredential` row (`Tunnels/TunnelCredentialStore`) of one household and one **server name**, a DNS label (`TunnelCredentialProtocol.IsServerName`: lowercase letters, digits and inner hyphens, at most 63), because the name goes into the server's synthetic address.

- The credential is `Tokens.NewSecret()` — 256 random bits — shown once by the mint and kept as its SHA-256 alone: with no dictionary to run, a slow hash buys nothing, and a lookup by hash, as a session's, could leak by its timing only how much of a hash matched.
- A server may hold more than one live credential, so one is replaced without a gap (mint, restart the server with it, revoke the old); a credential is revoked, never deleted.
- The command names a credential by id alone; a household's owners mint, list and revoke their own from the admin area (`.claude/rules/admin-owner-pages.md`, "Own servers"), where a revoke matches the household and the id together (`RevokeAsync(id, tenantId)`). The verbs are `accounts.md`'s "Commands".

`POST /accounts/internal/tunnels/verify` (`Internal/TunnelVerification`, `TunnelCredentialProtocol.VerifyPath`) is the relay's, asked on every tunnel's open: `TunnelCredentialQuery { credential, serverName }` — the name the tunnel claims — answered 200 `TunnelCredentialVerified { credentialId, tenantId, serverName }`, or 404 `TunnelCredentialRefused { reason }`, checked in this order: `Unknown` (never minted, or altered), `Revoked`, `AnotherServer` (a live credential minted for another server name), `Suspended` (a live credential of a household the operator suspended; it opens again once resumed). `LiveAsync`, the back-call's check, answers none for a suspended household too. The records are in `Domain/DTOs/Tunnel`, read by both ends.

Its one caller is `CALLERS__agent` (`InternalCallers.Agent`, whose only question is `InternalCallers.Tunnels`): the agent has no `/mcp`, so compose feeds it `${AGENTAPI__SHAREDSECRET}`, the credential the agent already holds for its own API. SexyTime's server holds that too, which lets it verify a tunnel credential only if it already has one, and a credential opens a tunnel by itself. A channel server's credential asking it is 403, and the agent's asking anything else is 403 too.

The same caller asks `POST /accounts/internal/tunnels/holders` (`TunnelCredentialProtocol.HoldersPath`, `TunnelServerHoldersQuery { serverName }`) for its key commands: every household holding a live credential for that name, suspended ones included, and the home whether it holds one or not, home first, each a `TunnelServerHolder` (`TunnelCredentialStore.HoldersAsync`; `.claude/rules/own-servers.md`, "The key commands reach every household's servers the same way"). A holder's `OwnerId` is the household's longest-standing enabled owner, as whom `redelegate` offers what its server keeps acting for nobody.

Announcements, on Redis — the stack's one way for a service to tell others something without knowing who listens (as `metrics:events` tells the dashboard):

- Revoking: `TunnelRevocationPublisher` publishes `TunnelCredentialRevoked { credentialId, tenantId, serverName }` as JSON on `TunnelCredentialProtocol.RevokedChannel` (`accounts:tunnel-credentials:revoked`), so the relay closes the tunnel that credential opened. Pub/sub reaches only who is subscribed then; the relay's verify on every open is what keeps a revoked credential out. Revoking one already revoked publishes again, so rerunning the command is the retry.
- Suspending a household publishes `HouseholdSuspended { tenantId }` on `TunnelCredentialProtocol.SuspendedChannel` (`accounts:households:suspended`), so the relay closes that household's open tunnels.
- A mint and a revocation are also announced to the configuration's readers (`HouseholdConfigProtocol.OwnServersChanged` on the saved channel), since the agent's view names the own servers a household holds a live credential for (`.claude/rules/household-config.md`).
- With `Redis:ConnectionString` empty (a test stack, a host run without Redis) nobody is told, and the command says so and exits 1 while the revocation stands.

## Tests

Tunnel credentials are driven through the commands and the verify route raw (`TunnelCredentialsTests`), with a `RedisFixture` to hear revocations; pub/sub ignores the leased database, so a test reads only the revocations naming its own credential.
