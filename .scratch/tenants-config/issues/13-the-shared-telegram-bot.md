# 13: The shared Telegram bot

**What to build:** One shared bot with the deployment's token serves every household: a linked person's message goes to their household's default agent and `/agent` switches the chat to another of the household's agents. The home household's jack and jonas bots come from its keys section, each bound to its agent. A group belongs to the household of the person who bound it. Spec: `../spec.md` ("Channels").

**Blocked by:** 06, 08

**Status:** ready-for-agent

- [ ] A linked person's message on the shared bot reaches their household's default agent
- [ ] `/agent` lists and switches among the household's agents for that chat
- [ ] The home's jack and jonas bots keep working from its configuration
- [ ] An unlinked person on the shared bot can only link
- [ ] A household-migration step seeds the home's jack and jonas bots into its keys section
