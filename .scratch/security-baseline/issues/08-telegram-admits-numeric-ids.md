# 08: Telegram admits numeric ids, and only the asker's tap answers

**What to build:** `AllowedUsernames` becomes `AllowedUserIds`, numeric, wired from `.env`. A message is admitted only when `From.Id` is on the list; the `Chat.Username` and `Chat.FirstName` fallbacks go. The turn's sender stays the username so memory and the identity map keep their keys until Accounts. The bot records the numeric id of the person whose message started each turn, and an approval tap counts only from that id; anyone else is told the approval isn't theirs. An approval asked by a fire is answered by any allowlisted person in the chat. Spec: `../spec.md` ("Telegram ids").

**Blocked by:** 07

**Status:** ready-for-agent

- [ ] An allowlisted id is admitted; a non-allowlisted id whose username or first name matches an old entry is refused (fake bot client)
- [ ] Another person's tap leaves the approval pending and gets "This approval isn't yours"; the asker's tap resolves it
- [ ] A fire's approval is answered by an allowlisted tap
- [ ] `.env`, `appsettings.json` and the Telegram rules name `AllowedUserIds`, and the startup log prints the ids
