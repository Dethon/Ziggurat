# 06: The principal carries the account (expand)

**What to build:** A chat-client turn reaches the agent as the person's account. The accounts service resolves a channel identity to tenant and user ids through an internal API called with each channel server's own credential; the principal gains the tenant and user ids beside today's form; the chat client stamps its turns with the account. Channels not yet moved keep working through the identity map. First step of expand, migrate, contract. Spec: `../spec.md` ("Channels and the principal").

**Blocked by:** 03

**Status:** done

- [x] The resolution API answers a linked identity with its tenant and user and refuses a caller without a channel credential
- [x] A chat-client turn's notification carries the account's tenant and user ids
- [x] A Telegram or voice turn still reaches the agent as before
- [x] A schedule written on a chat-client turn records the account in its creator

Notes for later tickets (`.claude/rules/channels.md`, `accounts.md`, `web-browsing.md` have the detail):

- `Principal(ChannelId, UserId, Account? Account = null)` with `Account(TenantId, UserId)` (`Domain/DTOs/Channel/Principal.cs`). `UserId` keeps its meaning — the channel's own name for the person — because the identity map, Telegram's `AnswerersOf`, `AsMeGate` and the eval harness match on it; 09 moves them to `Account.UserId`. Only the chat client sets `Account` so far.
- `POST /accounts/internal/resolve` `{ channel, identity }` → `{ tenantId, userId, displayName }` or 404; `signalr` and `voice` identities are the account id (08 can resolve a profile named after a user id today), `telegram` is always 404 until 07 writes the linked-identity rows (add the lookup in `IdentityResolution`), any other channel 400. Callers are `CALLERS__<host>` fed each server's own `MCP_SECRET_*`; compose already lists signalr, telegram, voice and Sugary. Caddy answers `/accounts/internal/*` 404 on every host that reaches the service.
- Every channel server asks through `IAccountResolver` (`Domain/Contracts`) → `AccountsResolverClient` (`Infrastructure/Clients/Accounts`, `services.AddAccountResolver(settings.Accounts, settings.Mcp.SharedSecret)`): null is nobody, an outage throws.
- A chat-client turn's `Sender` is the account's display name (asked once per hub connection; the account id when the service cannot say). Memory still keys by `Sender`, so WebChat memory now files under the display name until 10. Two things that must find the person read the account instead: `IAgentFactory.Create(..., Account? account)` looks custom agents up under `account.UserId`, and `RedisThreadStateStore.GetHistoryAsync` names a person's own message by the persisted principal's account (`ChatMessage.GetAuthorId()`), since the client compares to the account id.
- A user message's live `SenderId` (`UserMessageInfo`) and push subscriptions stay the account id.
