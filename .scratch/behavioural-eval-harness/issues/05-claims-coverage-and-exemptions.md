# 05 — Claims, coverage and exemptions

**What to build:** every falsifiable statement a prompt section makes becomes a declared
**claim** with an id, and an untested claim becomes visible instead of assumed (ADR-0031).

Claims are declared beside the prose that teaches them, aggregated by the prompt manifest the way
declarations already are, and cited by the scenarios that exercise them. A coverage test fails
when a declared claim has neither a scenario nor an entry in the exemption list, and each
exemption carries a reason.

The timer contract's claims are declared in full here, including the ones no scenario covers yet.
The existing prompt-rule vocabulary is left alone: a rule names a topic a section legislates for
conflict arbitration, a claim is an assertion about behaviour.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] Each claim has a stable id and a one-line statement, declared beside the prose it comes
      from.
- [ ] The manifest can enumerate every claim across every section.
- [ ] A scenario cites the claim ids it exercises, and citing an id that does not exist fails.
- [ ] A declared claim with no scenario and no exemption fails the coverage test.
- [ ] The exemption list names each uncovered claim with a reason.
- [ ] The timer contract's claims are declared in full, and the scenarios that exist cite them.
