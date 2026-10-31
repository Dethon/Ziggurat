# 14: Timers per household, ringing on channels

**What to build:** A timer records its owner and household. It rings on the household's satellites through its voice server when its target is a satellite and the household has one; otherwise it rings as an insistent alert on the person's channels (the chat client with its push notification, and Telegram), and dismissing it on any of them stops it on all. Spec: `../spec.md` ("Shared servers").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** done

- [x] A timer for a household without satellites rings on the person's chat client and Telegram
- [x] Dismissing it on one channel stops it on every channel
- [x] The home's timers still ring on its satellites
- [x] A timer is listed and dismissed only by its household
