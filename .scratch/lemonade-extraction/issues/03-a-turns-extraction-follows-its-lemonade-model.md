# 03 — A turn's extraction follows its Lemonade model

**What to build:** a WebChat user who picks a Lemonade model for a turn has that turn's memory extraction run on the same model on the Lemonade chat host, with usage charted under the Lemonade model's name at zero cost. A turn with no patch or an OpenRouter patch is extracted by the configured model exactly as today, and a deployment with no Lemonade chat host configured behaves exactly as today. The recall hook, which already builds the extraction request after the patch is stamped on the user message, copies a Lemonade model from the patch onto the request. The extractor, renamed so it no longer names a provider, takes the model as an optional argument and sets it on the chat options it sends. The memory module builds the extractor's chat client as the same routing client a turn uses: the existing OpenRouter client for the configured extraction model plus, when a host is configured, a Lemonade client for that host, truncating a Lemonade extraction to the shared two-window rule with the extraction pipeline's window as the caller's. A Lemonade model the model source no longer lists at send time is treated as absent and the extraction goes to the configured model with a debug log line. No reasoning effort is sent for an extraction on the box.

**Blocked by:** 01 — The two-window rule is shared; 02 — The box enforces a JSON-schema response format.

**Status:** ready-for-agent

- [ ] The extraction request carries an optional model, set only when the patch names a Lemonade model; the hook tests pin it present for a Lemonade patch and absent for an OpenRouter patch or no patch.
- [ ] The extractor takes an optional model and, when given, sends it as the model on chat options; when absent it sends none. The extractor tests pin both.
- [ ] The extractor is renamed to a provider-neutral name; the consolidator keeps its name.
- [ ] Through the worker with a mocked extractor: a request naming a Lemonade model is extracted with that model; a request naming a Lemonade model the source does not list is extracted with no model.
- [ ] The memory module resolves the extractor with a host configured and without one; without a host the extractor sends to OpenRouter exactly as before (composition test).
- [ ] A Lemonade extraction whose window exceeds the discovered model window is truncated to it and the truncation and token usage events carry the namespaced Lemonade id with zero cost.
- [ ] Telegram and voice paths, dreaming and subagents are untouched; the dreaming client is built exactly as before.
- [ ] Live check noted in the ticket's comments: pick a Lemonade model in WebChat, send a turn, and see the extraction request reach the box and a candidate stored.
