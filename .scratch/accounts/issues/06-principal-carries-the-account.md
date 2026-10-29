# 06: The principal carries the account; the identity map retires

**What to build:** An internal accounts API resolves a channel identity (Telegram numeric id, voice profile, forwarded WebChat user) to tenant and user ids, called with each channel server's own credential. `Principal` becomes `(TenantId, UserId, ChannelId)`, set by the channel on the notification; `Sender` stays a display name. `IdentityMap`, `Identity:People`, `Identity:VerifiedChannels` and `IDENTITY__PEOPLE__*` go. `AsMeGate`, the login handoff and the logins page read the principal's user id; voice login works again via WebChat. Rules (`web-browsing.md`, `channels.md`) follow. Spec: `../spec.md` ("Principal").

**Blocked by:** 01, 03

**Status:** ready-for-agent

- [ ] A WebChat turn and a Telegram turn of the same person carry the same user id (channel tests)
- [ ] `asMe` reaches the person's logins from either channel and refuses a principal with no user
- [ ] A voice handoff link is delivered to the person's WebChat
- [ ] Nothing references the identity map or its configuration
