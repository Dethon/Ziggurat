# 03 — Unfalsifiable claims rewritten

Status: resolved

A claim is supposed to be a falsifiable statement. `memory.removal-is-the-only-action` cannot be
violated by any run — there is no other memory tool to call — so it is restated as what it
guards: the agent never asks the user to repeat something memory already holds. Others get
trimmed to the half a run can witness, with the exemption reason updated. No prose changes that
add rules; this is claim bookkeeping.

## Answer

Narrower than sketched, deliberately. The two structurally unfalsifiable memory claims are
deleted rather than restated: `removal-is-the-only-action` is a description of the toolset whose
guard-content is already `recall-shapes-the-answer`, and `no-other-users-memories` is enforced by
the store, so no run can ever witness either. A comment in `MemoryPrompts` says why those two
sentences declare no claim. The remaining unfalsifiable entries (`url-comes-from-a-search`,
`urls-are-cited-only-in-writing`, surveying/reading-first) stay declared with typed exemptions —
they are unfalsifiable against this fixture or by testing philosophy, not by construction, and
deleting them would hide real rules.
