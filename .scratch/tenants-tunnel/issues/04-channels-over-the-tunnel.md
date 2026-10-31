# 04: Channel servers over the tunnel

**What to build:** A tunnelled server that is also a channel is connected by the agent when its tunnel opens and dropped when it closes, with today's registration, health checks and `channel_receive` long-poll, so a watch's fire, a login wake or a download's notification reaches the agent from a household's site. Spec: `../spec.md` ("Channels and turns").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A tunnelled channel's notification starts a turn, and its reply reaches it through the tunnel
- [ ] The agent connects a channel when its tunnel opens and drops it when it closes, without a restart
- [ ] A delegated fire through the tunnel acts for its person as before
