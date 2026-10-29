# 09: Telegram admits numeric ids; only the asker's tap counts

**What to build:** The bot admits a message only when its sender's numeric Telegram id is on the allowlist; the username and first-name fallbacks go, while the turn's sender stays the username until Accounts. An approval tap counts only from the person whose message started the turn, and anyone else is told it isn't theirs; a fire's approval is answered by any allowlisted person in the chat. Telegram approval ids become a full 128-bit GUID within the callback-data limit. Spec: `../spec.md` ("Telegram ids").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] An allowlisted id is admitted; a non-listed id whose username or first name matches an old entry is refused
- [x] Another person's tap leaves the approval pending and is told it isn't theirs; the asker's tap resolves it
- [x] A fire's approval is answered by an allowlisted tap
- [x] Approval callback data carries 32 hex characters and stays within Telegram's limit
- [x] The startup log lists the admitted ids, and the Telegram rules and placeholders name the numeric allowlist
