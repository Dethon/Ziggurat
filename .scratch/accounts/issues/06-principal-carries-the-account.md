# 06: The principal carries the account (expand)

**What to build:** A chat-client turn reaches the agent as the person's account. The accounts service resolves a channel identity to tenant and user ids through an internal API called with each channel server's own credential; the principal gains the tenant and user ids beside today's form; the chat client stamps its turns with the account. Channels not yet moved keep working through the identity map. First step of expand, migrate, contract. Spec: `../spec.md` ("Channels and the principal").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] The resolution API answers a linked identity with its tenant and user and refuses a caller without a channel credential
- [ ] A chat-client turn's notification carries the account's tenant and user ids
- [ ] A Telegram or voice turn still reaches the agent as before
- [ ] A schedule written on a chat-client turn records the account in its creator
