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

2026-08-19, later — The remaining four unfalsifiable entries are closed and the kind no longer
appears in the scorecard.

The vault pair (`tree-is-surveyed-before-creating`, `read-before-editing`) got the memory pair's
treatment: deleted as claims with the reason written in `VaultPrompt` — they describe means, the
suite tests outcomes, and the outcomes are already `new-note-fits-the-tree` and the edit
scenarios' file assertions.

`web.url-comes-from-a-search` got its trap: "a named site is searched for, never guessed at"
names the site colloquially, anchors the only tolerated browse to the loopback url only a search
result can know, and fails a fabricated domain as an unnecessary call. 3/3 armed.

`web.urls-are-cited-only-in-writing` got its written half: the chronicle's written reply must
carry the source path. The first armed run exposed that the prose said less than the claim —
"cite only when written" reads as a restriction, not an obligation — so the sentence now says a
written reply names the page it answered from, and the run after that exposed a matcher edge:
the mentions boundary rejects a path preceded by the port's digit, so the spelling drops the
leading slash. 3/3 armed after both. Also: the delegation reflex reached the chronicle's turn
shape, which now tolerates the one worker its siblings tolerate.
