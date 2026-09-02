# 05 — A Lemonade extraction waits its grace

**What to build:** a WebChat user's own answer reaches the box's single slot before that turn's extraction does. A request naming a Lemonade model is not sent until a fixed grace has passed since it was enqueued; a request with no Lemonade model is processed immediately as today. The grace is one integer setting in seconds under the memory extraction section of the agent's appsettings, default 30, a generic tunable with no compose or `.env` entry. The worker measures it with the host's time provider. A waiting request waits off the worker's main loop, so other requests keep flowing; two extractions for one user may then run concurrently, which the store's novelty check already tolerates. The vanished-model check from ticket 03 happens when the grace expires, not when the request is enqueued.

**Blocked by:** 03 — A turn's extraction follows its Lemonade model.

**Status:** ready-for-agent

- [ ] The extraction options carry the grace; the memory module binds it from the memory extraction settings section; the agent settings test pins the shipped default at 30 seconds.
- [ ] Through the worker with the armed clock: a Lemonade request enqueued at time zero is not extracted before the grace and is extracted once the clock passes it.
- [ ] A plain request enqueued behind a waiting Lemonade request is extracted straight away.
- [ ] A Lemonade request whose model has vanished from the source by the time the grace expires is extracted with no model.
- [ ] Cancellation of the host during a wait ends the wait without an error being logged.
- [ ] No test sleeps.
