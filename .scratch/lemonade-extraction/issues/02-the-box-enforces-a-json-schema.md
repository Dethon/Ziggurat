# 02 — The box enforces a JSON-schema response format

**What to build:** a caller that asks the Lemonade chat host for a JSON-schema-shaped answer gets one the box's grammar enforces. The Lemonade client's wire rewrite, which already rewrites the model id and drops replayed reasoning items, also moves a JSON-schema response format from the Responses wire's `text.format` field into a chat-completions-shaped `response_format` node on the same body (type `json_schema`, holding the same name, schema and strictness), because the box ignores the former and honours the latter (verified live, streamed and non-streamed, thinking on and off). A body with no response format, or a `text.format` of any other shape, is left alone. OpenRouter requests are untouched.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Through the routing client over the capturing Lemonade stream handler, a request whose chat options carry a JSON-schema response format reaches the host with a `response_format` node of type `json_schema` carrying the same name, schema and strict flag, and no `text.format` node.
- [ ] A Lemonade request with no response format reaches the host with a body identical to today's.
- [ ] A Lemonade request whose `text.format` is not a JSON schema reaches the host with that node unchanged.
- [ ] An OpenRouter request carrying the same response format reaches OpenRouter byte-for-byte as the bare client sends it.
- [ ] The Lemonade client's header comment names the rewrite and why the box needs it.
- [ ] Prior art followed: the routing client tests and the captured-stream handler.
