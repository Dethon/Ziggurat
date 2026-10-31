# 05: Offline servers

**What to build:** A conversation's tool sessions are reconciled at each turn's start: a household's server that came online joins, one that went offline leaves, and one line of the turn's context names the household's servers that are offline. A call in flight when its tunnel drops fails with an error saying the server went offline. An operator command lists a household's connected servers, since when, and the tools each offers. Spec: `../spec.md` ("Channels and turns").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A turn built while a server is offline offers none of its tools and names it in its context
- [ ] A server that comes online is offered on the next turn of the same conversation
- [ ] A call in flight when the tunnel drops fails with the went-offline error
- [ ] The operator command lists connected servers with their connection time and tools
