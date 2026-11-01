# 13: The shared Telegram bot

**What to build:** One shared bot with the deployment's token serves every household: a linked person's message goes to their household's default agent and `/agent` switches the chat to another of the household's agents. The home household's jack and jonas bots come from its keys section, each bound to its agent. A group belongs to the household of the person who bound it. Spec: `../spec.md` ("Channels").

**Blocked by:** 06, 08

**Status:** done

- [x] A linked person's message on the shared bot reaches their household's default agent
- [x] `/agent` lists and switches among the household's agents for that chat
- [x] The home's jack and jonas bots keep working from its configuration
- [x] An unlinked person on the shared bot can only link
- [x] A household-migration step seeds the home's jack and jonas bots into its keys section

Notes: the list comes from a new accounts view, `agent-names` (Telegram's, on the person's assertion),
not from the catalogue the agent registers. A chat's pick is kept in Redis per household and chat. A
group already belonged to the household whose owner's code bound it; the shared bot's tests pin it.
For 15: deploy with `TELEGRAM_SHARED_BOT_TOKEN` set, then run the migration with
`--telegram-bots - < home-bots.json` (jack's and jonas's tokens, the old `BOTS__0`/`BOTS__1`) before any
`keys` save — the Idealista key included — and only then delete the old `BOTS__*` lines from prod's
`.env`. Until the migration runs, jack's and jonas's bots are silent.
Superseded by 15: the input is `--home-keys -` (the bots and Idealista's pair), and the order no
longer matters; see `../deploy.md`.
