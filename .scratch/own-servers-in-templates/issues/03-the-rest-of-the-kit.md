# 03: The rest of the kit — sandbox, web search, printer

**What to build:** The Jonas template names `sandbox`, `websearch` and `printer` beside `vault`, with
what the home's seeded Jonas pairs them with (the web search tools on its whitelist; the filesystem
feature it already has). A household that set up any of them has its agent use it — run a command in
its sandbox, search the web from its own connection, print at home — and one that did not is never
told about it. Spec: `../spec.md`.

**Blocked by:** 02 (a household's agents reach the vault it set up)

**Status:** ready-for-agent

- [ ] Each of sandbox, web search and printer: set up and connected is reached; set up and off is named offline; not set up is absent
- [ ] Web search's `web_login` from a household other than the home is not promised (its handoff is home-only, spec "Out of Scope"); searching and reading pages work
- [ ] The Sugary template is unchanged
- [ ] Prompt snapshots regenerate; the eval family whose scenarios use the template's agent, or the full tier, is run if its prompt changed
