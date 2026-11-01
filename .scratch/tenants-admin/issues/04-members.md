# 04: Members

**What to build:** Owners invite a person by passkey link or Telegram code, set a member's role and sees-all, disable and re-enable a member, and mint a member's recovery link. Disabling revokes their sessions, stops their links and Telegram and voice identities resolving, and stops delegations naming them from acting; their data stays. Spec: `../spec.md` ("Members").

**Blocked by:** 01

**Status:** done

- [x] An invited person joins the household by passkey or Telegram
- [x] A disabled member cannot sign in, is not recognised on Telegram or voice, and their schedules fire as nobody
- [x] Re-enabling restores all of it, with their memories and topics intact
- [x] Role and sees-all changes take effect on the member's next turn — in the turn's assertion (the hub's
  per-turn session refresh, voice's per-utterance resolution); the chat hub's topic view keeps the
  connection's sees-all until the chat client next loads, and Telegram an admitted sender's roles for up
  to `LinkCache:Linked`

Decisions (`.claude/rules/admin.md`, "Members"; `.claude/rules/accounts.md`, "Disabled members"):
disabling is one column (`User.DisabledAt`) plus revoking the member's sessions; every store that vouches
for a person refuses a disabled one itself (sessions, links, `resolve`, Telegram's person lookups), so
nothing is deleted and re-enabling is clearing it. **The agent learns from its own household view**
(`AgentHouseholdConfig.DisabledPeople`, dropped on a `PeopleChanged` announcement) and checks it in the
conversation group for a person's turn as well as a delegated fire (`IAccountStanding`): rather than
accounts refusing the fire's household-config proof, which only gates the scheduling server's zone read
and cannot change whom the fire acts for. Checking turns too is what bounds Telegram's `LinkCache:Linked`
(5 minutes, an assertion lasting 10): the bot keeps forwarding a disabled member's messages that long and
the agent refuses each with a message. Refusals answer 400 (last enabled owner; a recovery link for a
disabled member), 403 (the operator: never disabled or recovered by an owner). Dreaming still walks a
disabled member's memories.
