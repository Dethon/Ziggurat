# 12: Telegram asks for review copies

**What to build:** A person's Telegram conversations sit among their topics in the chat client. Telegram asks for a review copy on every turn; a private chat's copy belongs to the person, a group's is shared. Spec: `../spec.md` ("Channels and the principal", Telegram; "Topics").

**Blocked by:** 07, 11

**Status:** done

- [x] A private-chat turn produces a review-copy topic owned by the sender
- [x] A group turn produces a shared review-copy topic
- [x] The review copy carries the turn's files as other review copies do

Notes for later tickets (`.claude/rules/channels.md` has the detail):

- Every Telegram turn carries `ReviewCopy`: `Telegram: <account display name>` in a private chat, `Telegram: <group title>` with `Group = true` in a group. `Group` is a fact about the conversation, never a destination; it rides to `create_conversation` as `group` (`IChannelConnection.CreateConversationAsync` gained the parameter), and `ConversationFactory` makes a group's topic `OwnerId` null, `Shared` true, `TenantId` the sender's household. The turn's principal still carries the sender's account.
- Unowned-but-tenanted is a third topic shape (16: the migration must not read a tenant as "owned").
- Telegram's attachment references are now `<conversation dir>/<agentId>.<file_id>` (`TelegramReference`), because WebChat's store refuses a review copy whose id does not begin with its conversation's directory; under the old `<agentId>/<file_id>` every Telegram review copy would have shown placeholders for its photos. `fetch_attachment` still reads the old spelling in persisted histories.
- No E2E was run for this ticket (by instruction); a live check of a Telegram photo in a review topic is pending.
