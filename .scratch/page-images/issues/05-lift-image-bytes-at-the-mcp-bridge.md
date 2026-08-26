# 05 — Lift image bytes at the MCP bridge

Status: resolved

**What to build:** When a tool running in a separate server answers with a picture, the picture reaches the model but its bytes never enter the conversation history. The bridge between the two takes the bytes out, puts them in the agent's own store, and leaves behind the text that stands for them — so reading a history costs the same whether or not pictures were ever looked at, and the browse server keeps knowing nothing about the agent's storage.

Every future server returning an image inherits this without asking for it.

Blocked by: 04

- [x] An image block returned by an MCP tool has its bytes moved into the agent's read-image store
- [x] What the bridge returns in its place carries the envelope text and no bytes
- [x] The bytes are stored under the same conversation-and-call key the filesystem read tool uses, so hydration finds them unchanged
- [x] A host with no store configured passes the result through exactly as it does today, without failing
- [x] The lifting happens where the conversation context is already resolved, not in the pure flattening helper that has no access to it
- [x] Existing all-text multi-block results still flatten to a single string as before
- [x] Covered by unit tests constructing the bridge with a fake store and a stub inner tool, including the null-store path
