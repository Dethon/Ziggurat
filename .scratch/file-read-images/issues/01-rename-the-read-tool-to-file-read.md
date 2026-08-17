# 01 — Rename the read tool to `file_read`

**What to build:** nothing changes for anyone using the agent. The filesystem read tool is named
for what it is about to become: a tool that reads a file, whatever kind it is. The model sees
`file_read` where it saw `text_read`, a mount's capability list says `file_read`, and everything
that selects, dispatches or tests the tool follows. This is the prefactor that keeps the next
ticket a small diff.

The feature key stays `read` and the wire operation stays `fs_read`, so existing agent
configuration keeps selecting the tool and every outpost binary already copied onto somebody's
machine keeps advertising it — capability strings are derived hub-side from wire tool names, so
nothing remote has to be rebuilt.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The domain tool type is renamed with its subject, and its model-facing leaf name is
      `file_read`.
- [ ] The capability string a mount publishes for this operation is `file_read`.
- [ ] The feature key remains `read`; an agent configured with it still gets the tool.
- [ ] The wire operation remains `fs_read`; no backend method, MCP tool name or published
      filesystem resource changes.
- [ ] The tool's description still describes text reading accurately — no image claims yet.
- [ ] The existing tool test suite is renamed with its subject and passes unchanged.
- [ ] The virtual-path conformance suite and the tool-feature suite pick up the new name and pass.
- [ ] The virtual filesystem rules file names `file_read` wherever it named `text_read`.
- [ ] No behaviour change: reading a text file returns exactly what it returned before.
