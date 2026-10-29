# 08: Voice resolves to accounts; a guest acts as nobody

**What to build:** nabu acts as the person it recognizes and as nobody otherwise. A speaker profile is named after its user id and a conclusive match is that account; no match is the voice guest, a principal with no user, who can still control the house, set timers and ask questions but reaches no person's memory, logins or Sugary log. The enrollment script and the voice rules say so. Spec: `../spec.md` ("Channels and the principal", Voice).

**Blocked by:** 06

**Status:** done

- [x] A matched speaker's turn carries their account
- [x] An unmatched turn carries a principal with no user
- [x] A guest turn can still call house, timer and question tools
- [x] The enrollment script names a profile by user id

Notes for later tickets (`.claude/rules/voice.md` "Who a turn acts for" has the detail):

- A recognised speaker's principal is `Principal("voice", <user id>, Account(tenant, user))` with the display name as `Sender`; the guest's is `Principal("voice", "household")` with no account and the satellite's `Identity` as `Sender`. 09: the identity map's voice entries now only match if they hold the user id; reading `Account.UserId` retires that. 10 turns memory off for the guest by its missing account; 11 gives its review copy a shared topic.
- A profile enrolled under a name before accounts (e.g. `/voices/fran`) is nobody's account and its speaker is the guest (logged): prod's existing folders must be renamed to their person's user id after the migration.
- An accounts outage costs a turn up to `Accounts:RequestTimeout` (5 s) before the guest fallback; resolution runs beside `create_conversation`, not after it.
