# 07: Telegram links to accounts

**What to build:** Someone who only uses Telegram can have a full account, and every Telegram turn is the account's. The start parameter redeems an invite, creating the account, or a link code from the account page; a web-link command sends a linked person a link that adds a passkey; an owner's bind command binds a group to the household; an unlinked sender, private or in a group, is refused with a hint to ask an owner for an invite. Linked identities replace the baseline's numeric allowlist. Spec: `../spec.md` ("Channels and the principal", Telegram).

**Blocked by:** 05, 06

**Status:** done

- [x] Redeeming an invite through the start parameter creates a linked account; a used or expired code is refused
- [x] The web-link command answers a one-time passkey link to a linked person only
- [x] An unlinked sender is refused; a bound group admits linked members as their own turns
- [x] A Telegram turn's notification carries the account's tenant and user ids

Notes for later tickets (`.claude/rules/accounts.md` and `channels.md` have the detail):

- The store has `LinkedIdentities` (`Channel`, `Identity`, unique per identity and per person and channel) and `BoundChats` (`Channel`, `ChatId`, `TenantId`), generic by channel; migration `TelegramLinks`. `resolve` answers `telegram` from those rows.
- Every `/accounts/internal/*` endpoint sits behind one caller filter on the route group (`Internal/InternalApi`). Telegram's are `/accounts/internal/telegram/{start,web-link,bind,group}`, asked through `ITelegramAccounts` (`Domain/Contracts`), which the same `AccountsResolverClient` answers; `AddAccountResolver` registers both contracts.
- `LinkKind.TelegramGroup` is an owner's group code ("Bind a Telegram group" on the members page). An invite on the members page is shown as the bot's deep link too.
- `/web` refuses the operator (403 → the bot tells them to use their account page or the container command): their Telegram must not be a way onto the dashboard, as the members page refuses them a recovery link. Narrower than "every linked person"; deliberate.
- A Telegram turn's principal is `Principal("telegram", <username or id>, Account(tenant, user))`. `UserId` and `Sender` keep the username, so the approval router (`SenderId`), the identity map and memory are unchanged; 09 and 10 move them onto `Account.UserId`.
- A fire's approval in a Telegram chat (one naming no Telegram person) is answerable by any linked member of the chat's household: a private chat's id is its person's own Telegram id, a group's household is its binding. Each tap is resolved as it comes, through the same cache (`ApprovalCallbackRouter.RegisterApproval` takes a predicate now). 13 makes an approval its asker's across channels.
- `TELEGRAM__BOTUSERNAME` is a compose entry of `accounts`, shipped **empty** rather than `changeme`: a placeholder bot name would turn every link code into a deep link to a stranger's bot. The operator sets it when deploying.
- The bot caches resolutions (`LinkCache`: 5 min linked, 5 s refused, `TimeProvider`); a redemption or bind seeds the cache. An unlinked person in a forum-topic group is answered on every message, as the spec asks — if that proves noisy, rate-limit the refusal per sender.
