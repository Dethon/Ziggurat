# 06 — Vault moves

**What to build:** The vault section splits by the timing rule: choosing rules and their claims stay as a standing stub served by the mcp-vault server; the doing rules become one skill the same server publishes. The skill's description is a trigger claim cited by the vault scenarios, which require the load. Claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. If the stub would be under the size floor and the section has no choosing rules, the section moves whole and the stub is one sentence.

**Blocked by:** 05

**Status:** done

- [x] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [x] The server serves the stub as its prompt and the skill as resources; agent snapshots show the stub and the advertisement; the body snapshot is under its budget.
- [x] The family's scenarios cite the trigger claim and require the load; mechanism scenarios still pass with no load required.
- [x] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [x] A full-tier pass after the move shows no claim rate below the backed-up scorecard; the ceiling is lowered to remaining plus headroom.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Rollout.

Result (2026-09-08): base 73/73, 223/228 (`…after-home-skill-c.json`); after the move 73/73, 221/228 (`…after-vault-skill-c.json`), one spurious load in 228. The vault family was green from the first pass; two passes reddened the watch family instead — runs that skipped the setup index and wrote the watches skill's own example entity id — traced to the watches stub sentence ticket 05 added ("the setup index names the entity, so do not load home-assistant"), which read as skip-to-the-write. The sentence now says the index read comes first, the example is a placeholder, and watch scenarios require the index read as the home family does. Demonstrated red in `…vault-body-deleted.json`: rename-updates-incoming-links 0/3, headings-are-referenceable 1/3, no-new-top-level-folder 0/3, irreversible-change-is-asked-about 0/3, writes-are-text-only 0/3, load on every run; frontmatter, templates, daily notes and attachments stayed green without the body and became guards. Nabu 10,567 → 9,212 tokens; ceiling 14,600 → 13,200.

