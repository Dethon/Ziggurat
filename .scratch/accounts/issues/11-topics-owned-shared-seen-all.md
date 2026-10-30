# 11: Topics are owned, shared by choice, seen all by permission

**What to build:** A person's topics are private unless they share them. A topic records its owner and a shared flag only the owner sets; the hub lists, reads, searches, continues, cancels, deletes and approves only on topics the person may see: their own, shared ones in their household's spaces, and every one with "sees all conversations". A review copy's owner comes from the turn's principal, chosen by the review host, so a guest's is shared; the channel still never names where it goes (ADR 0046). A sees-all member sees approvals in others' topics but can't answer them. The chat client shows a share toggle on your own topics. Spec: `../spec.md` ("Topics").

**Blocked by:** 05, 06, 08

**Status:** done

- [x] Another person's private topic is absent from lists and search and refused by every hub call
- [x] Sharing makes a topic visible to the household and continuable, a continuation being the continuer's own turn; unsharing hides it again
- [x] A member with "sees all conversations" sees every topic, review copies included, and can't answer another's approval
- [x] Voice and Sugary review copies are owned by the turn's person; a voice guest's is shared (the review host owns a copy by the turn's principal; Sugary's turns carry no account until 14, so its copies are shared until then)

Notes for later tickets (`.claude/rules/channels.md` "A topic is its owner's unless they share it" has the detail):

- The permission rides as the `sees-all` role in `X-Ziggurat-Roles`, read once per connection: a grant or withdrawal reaches an open chat client on reconnect.
- Decided beyond the ticket: seeing every topic is reading only. Another person's private topic is theirs alone to continue, rename, delete, stop, upload into or mark read (`TopicViewer.MayChange`); a shared topic is the household's to change, delete included. A private topic's finished-reply push goes to its owner's devices alone.
- 13: until an approval records its asker, the hub lets the topic's owner answer, on a shared topic also whoever's turn is running (the stream's current sender), and on an unowned one anyone who sees it; anyone else gets false and the prompt stays pending (`ApprovalService.TopicOfApproval` is the hook). A sees-all member's modal still shows the buttons, and the client clears it locally on the false; nor does the composer go read-only on a topic the viewer may only read.
- 14: Sugary's review copies are shared today because its turns carry no account. Once they do, the copy is private to that person, and `Tests.E2E.Sugary.SugaryE2ETests.TheReviewTopicShowsTheEstimateAsync` (which reads it as `fixture.NextUserIndex()`, another account) must read it as the account the app's turn acted for.
- 12: Telegram's turns hand their principal to `create_conversation` already; a private chat's copy is its person's as soon as the principal carries the account, and a group's must reach the host as a principal with no account (or a shared flag of the host's choosing) to be shared.
- 16: a migrated topic has no `OwnerId`, and only an owner may change `Shared`, so marking legacy topics shared without assigning owners leaves nobody able to make one private. Topics with no owner already read as shared, so the migration's real job is assigning owners (the creator is not recorded; the topic's first `user` message's persisted principal is the only trace).
- Voice now waits for the speaker before minting a new conversation (its topic is the speaker's); later turns still overlap the resolution. A conversation keeps the owner its first speaker gave it.
- `IChannelConnection.CreateConversationAsync` takes the turn's `Principal?`; voice's and SignalR's `create_conversation` tools take `principal`.
