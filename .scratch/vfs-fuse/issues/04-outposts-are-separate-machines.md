# 04 — Outposts are separate machines

**What to build:** The agent sees each outpost at `outpost:<NAME>/...`, not `/<name>`, and the prompt describes outposts as separate machines rather than branches of one tree. Every other mount keeps `/<mount identity>`. Because outposts no longer claim a mount point, a shadowed outpost can only be shadowed by another outpost: a machine named `vault` no longer competes with the vault. An outpost's own exec keeps its `Host` reach and screening.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [x] Every file tool resolves and answers outpost paths in the `outpost:<NAME>` spelling (virtual-path conformance covers it)
- [x] Two outposts with the same name: the second is shadowed and its verdict travels home, as today
- [x] An outpost named like an existing mount is mounted, not shadowed
- [x] The prompt snapshot presents outposts as separate machines
- [x] The exec screen still treats an outpost's exec as `Host` reach
- [x] The virtual-filesystem and outpost rules, and the glossary's **Shadowed outpost**, describe the new spelling
