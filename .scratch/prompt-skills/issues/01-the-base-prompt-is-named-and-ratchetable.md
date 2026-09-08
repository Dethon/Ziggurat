# 01 — The base prompt is named and ratchetable

**What to build:** A prefactor with no behaviour change, so the first skill lands on prepared ground. The file that holds the core directive carries its section's name, so "base prompt" can mean the standing whole. The per-agent ceiling is expressed as what remains plus a fixed headroom, so a later move lowers it by editing one number and the worst-case budget test keeps the sum honest. The prompts rule file states the dividing line and the procedure: a rule the model needs before choosing stays, a rule it needs while doing moves; a section under about 500 tokens stays whole; a claim in a skill body is demonstrated red with the body's prose deleted and the description intact.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The core directive's file and type carry the section name; every snapshot is byte-identical to before.
- [ ] The ceiling is a remaining-plus-headroom expression; the budget tests pass unchanged and a test shows lowering the remaining figure fails an agent whose declared sections exceed it.
- [ ] The prompts rule file carries the timing rule, the size floor and the skill-claim red procedure, and names the glossary terms base prompt, skill and trigger claim.
- [ ] Spec: `.scratch/prompt-skills/spec.md` § The dividing line, § The manifest.
