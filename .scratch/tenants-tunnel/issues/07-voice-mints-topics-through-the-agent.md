# 07: Voice mints topics through an agent API

**What to build:** Voice stops writing the deployment's Redis: the topics its turns need are minted by the agent through an internal API, for every voice server, so a household's voice server needs no deployment database. Spec: `../spec.md` ("Back-calls").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A voice turn's topic is minted through the agent API and appears in the chat client as before
- [ ] The voice server holds no connection to the deployment's Redis for topics
- [ ] The voice and chat-client review-copy tests pass
