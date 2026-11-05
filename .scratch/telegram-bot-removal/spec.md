# An owner can remove a household's Telegram bot

Status: needs-triage

Found in manual test 13 (2026-11-05). The Keys page binds a household Telegram bot to one of its agents
(`PUT /household/keys/telegram-bots/{agentId}`), and since ef3a476dc saving the same token under another
agent moves it. Nothing removes one: there is no `DELETE` route and no button, so a bot an owner no
longer wants — a token they revoked at BotFather, a bot they gave away — stays polled by the Telegram
channel for as long as the household exists, and the agent it answers as can never be removed (an agent
a bot answers as is refused removal, user's decision).

## What is wanted

- `DELETE /accounts/admin/household/keys/telegram-bots/{agentId}` drops that binding from the sealed
  keys section, recorded ("Removed the Telegram bot of <agentId>") and announced like any keys save, so
  the channel stops polling it.
- The Keys page lists each bound bot with Remove behind a confirmation.
- The refusal to remove an agent a bot answers as names both ways out: bind the bot to another agent,
  or remove the bot.

## Open questions

- Should a bot whose token BotFather revoked (getMe 401 on the channel's next poll) be marked on the
  Keys page, so the owner knows to remove it?
