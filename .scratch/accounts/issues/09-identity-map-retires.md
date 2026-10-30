# 09: The identity map retires (contract)

**What to build:** Browsing as a person follows the account on every channel, and the identity map is gone. The asMe gate, the login handoff and the logins page read the principal's user id; remembered logins are sealed with the user id as associated data; the identity map, its people and verified-channel settings and its environment variables are removed; the chat client counts as verified, so a voice handoff link reaches the person's chat client again. The web-browsing and channel rules follow. Spec: `../spec.md` ("Channels and the principal", Retirements; "Keyed by user id", Web logins).

**Blocked by:** 07, 08

**Status:** done

- [x] asMe reaches a person's remembered logins from Telegram and from the chat client, and refuses a principal with no user
- [x] A login saved by one person can't be opened under another's user id
- [x] A voice login handoff delivers its link to the person's chat client
- [x] Nothing references the identity map or its configuration

Notes for later tickets (`.claude/rules/web-browsing.md` "Browsing as a person" has the detail):

- `PersonResolution.Of(context)` (`Domain/Tools/Web`) is the whole resolution: the principal's `Account` is the person, its `UserId` the key of their browser context, live-only logins, logins page and sealed record. No account is nobody: the voice guest on its own turn is told its voice was not recognised; anything else (an unstamped fire, a person's own turn with no stamped principal, a principal from before accounts) is "nobody vouched". There is no verified-channel list; a principal from any channel carrying an account resolves.
- **16:** remembered logins now live at `websearch:person:{userId}`, sealed with the user id as associated data; records under `websearch:person:{name}` are never read as a fallback, so the migration must open each with the name as associated data and re-seal it under the user id (the same `Logins:StateKey`). The people it maps come from prod's old `IDENTITY__PEOPLE__<n>__{NAME,TELEGRAM,WEBCHAT,VOICE}` values, which the browse server no longer reads and the shipped compose file no longer lists — the operator has them in prod's own compose environment.
- **11:** a voice login's link is a fire (`LoginWake.LinkInChatClient`, origin `Login`, reply target `signalr` with no conversation) whose principal is the voice turn's own `(voice, user id, Account)`, with `UserId` and `Sender` the account's user id (this server learns no display name). SignalR still mints it in `space:default` like any fire; making the minted topic the person's is reading the owner off that principal. The same holds for any fire into the chat client (a schedule's `CreatedBy`).
- The handoff link and the logins page keep their link-claimed cookies; a claim is not bound to the accounts session.
