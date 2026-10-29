# 07: Telegram links to accounts

**What to build:** `/start <code>` redeems an invite (creating the account) or a link-Telegram code from the account page, recording the numeric id. `/web` sends a linked person a one-time link to add a passkey. An owner binds a group to the household with `/bind <code>`. A message from an unlinked person, private or in a group, is refused with a hint to ask an owner for an invite. The baseline's `AllowedUserIds` gives way to linked identities. Telegram asks for a review copy on every turn: a private chat's is the person's, a group's is shared. Spec: `../spec.md` ("Telegram").

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] Redeeming an invite through `/start` creates a linked account; a used or expired code is refused (fake bot client)
- [ ] `/web` answers a one-time passkey link to a linked person only
- [ ] An unlinked sender is refused; a bound group admits linked members as their own turns
- [ ] A Telegram turn produces a review-copy topic owned by the person
