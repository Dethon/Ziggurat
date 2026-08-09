# 02 — Attachment capability discovery

**What to build:** The system knows, without anyone configuring it, which models accept images
and which accept documents. The agent asks the model provider for its model list at startup and
again every hour, keeps the accepted input kinds for each model it cares about, and publishes
them with the agent catalogue it registers with every channel — for the agent's own default
model and for each model a person is allowed to switch to.

When the lookup fails, the last values that worked are used. When it fails with nothing ever
cached, capability is treated as permissive: a transient problem at the provider must not remove
a feature from everyone. Nothing consumes this yet; it is verifiable by inspecting the
catalogue a channel receives.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Accepted input kinds are read from the provider's model list for the agent's default model and every patchable model.
- [ ] The values are cached and refreshed hourly without a restart.
- [ ] A failed refresh leaves the previous values in place.
- [ ] With no cached values and a failed first lookup, capability is permissive.
- [ ] The agent catalogue carries the accepted kinds per model and reaches channels through the existing registration, including on reconnect.
- [ ] Tests drive the lookup against a stubbed provider response, covering parsing, refresh, last-known-good and the permissive fallback.
