# 04 — Lemonade models appear in the selector

**What to build:** An operator sets the Lemonade chat host's address in the new `lemonadeChat` settings section and, within about a minute, every agent that offers patchable models also offers the box's chat models in WebChat's gear menu. Each entry is a patchable model with a namespaced id (`lemonade/<id>`), a display name trimmed at the first `-GGUF` (full id when two would collide), and an image attachment kind only when the box labels the model vision. Only models labelled both chat and tool-calling, downloaded, and not proxied from a cloud provider are offered. When the box cannot be asked, the entries are absent and one warning is logged. With an empty address the feature does not exist: no discovery, no probe, no warning. The address gets placeholder environment entries in compose for the agent and observability containers and no compose service, and the observability dashboard gains a health probe row for the host that reads red when it is down. Picking one of these models does not yet route anywhere new; that is the next ticket.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] With an address configured and a reachable host, the agent catalog every channel receives lists the box's kept models after the configured patchable models, for every agent that has any, with namespaced ids, trimmed names and vision-derived attachment kinds
- [ ] Two kept ids that trim to the same name are both shown by their full id
- [ ] Models lacking the chat or tool-calling label, not downloaded, or naming a cloud provider are not offered
- [ ] The discovered context window (`context_length`, else `max_context_window`, else unknown) is kept on the source for the next tickets to read
- [ ] The source refreshes once at startup and about every minute; the existing catalog re-registration carries a changed list to WebChat without any new hub call
- [ ] An unreachable, erroring or malformed host offers nothing, logs one warning, and keeps offering nothing until an answer arrives
- [ ] An empty address makes no request, logs nothing and offers nothing, and the agent appsettings test pins the shipped default as empty
- [ ] An optional API key is sent as a bearer token when set and the header is omitted when blank
- [ ] Compose carries placeholder environment entries for the address and no new service; the observability probe list carries the host's health endpoint only when an address is set
- [ ] Tests: the catalog builder with a stub Lemonade model source, and the discovery source against a WireMock models endpoint, following the OpenRouter capability tests
