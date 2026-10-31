# 14: Timers per household, ringing on channels

**What to build:** A timer records its owner and household. It rings on the household's satellites through its voice server when its target is a satellite and the household has one; otherwise it rings as an insistent alert on the person's channels (the chat client with its push notification, and Telegram), and dismissing it on any of them stops it on all. Spec: `../spec.md` ("Shared servers").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] A timer for a household without satellites rings on the person's chat client and Telegram
- [ ] Dismissing it on one channel stops it on every channel
- [ ] The home's timers still ring on its satellites
- [ ] A timer is listed and dismissed only by its household
