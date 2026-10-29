# 07: Telegram links to accounts

**What to build:** Someone who only uses Telegram can have a full account, and every Telegram turn is the account's. The start parameter redeems an invite, creating the account, or a link code from the account page; a web-link command sends a linked person a link that adds a passkey; an owner's bind command binds a group to the household; an unlinked sender, private or in a group, is refused with a hint to ask an owner for an invite. Linked identities replace the baseline's numeric allowlist. Spec: `../spec.md` ("Channels and the principal", Telegram).

**Blocked by:** 05, 06

**Status:** ready-for-agent

- [ ] Redeeming an invite through the start parameter creates a linked account; a used or expired code is refused
- [ ] The web-link command answers a one-time passkey link to a linked person only
- [ ] An unlinked sender is refused; a bound group admits linked members as their own turns
- [ ] A Telegram turn's notification carries the account's tenant and user ids
