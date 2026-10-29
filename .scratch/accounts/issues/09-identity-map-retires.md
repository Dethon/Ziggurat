# 09: The identity map retires (contract)

**What to build:** Browsing as a person follows the account on every channel, and the identity map is gone. The asMe gate, the login handoff and the logins page read the principal's user id; remembered logins are sealed with the user id as associated data; the identity map, its people and verified-channel settings and its environment variables are removed; the chat client counts as verified, so a voice handoff link reaches the person's chat client again. The web-browsing and channel rules follow. Spec: `../spec.md` ("Channels and the principal", Retirements; "Keyed by user id", Web logins).

**Blocked by:** 07, 08

**Status:** ready-for-agent

- [ ] asMe reaches a person's remembered logins from Telegram and from the chat client, and refuses a principal with no user
- [ ] A login saved by one person can't be opened under another's user id
- [ ] A voice login handoff delivers its link to the person's chat client
- [ ] Nothing references the identity map or its configuration
