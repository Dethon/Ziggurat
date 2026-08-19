# 02 — Vault: six scenarios and two seed additions

The two `needs-fixture` entries close with seeds, not harness work: the "binary the eval cannot
hand the agent" already sits in the vault (the seed writes `attachments/pesto.png`), so an
attachment scenario references a seeded file instead of uploading one; and the no-new-top-level
rule needs an `Inbox/` folder plus a topic that fits nothing.

Seed additions (`EvalVault`): `Inbox/Ideas sueltas.md`; `attachments/tarta.jpg`;
`Cocina/Trucos.md` linking `[[Pasta al pesto#Variantes]]`; a `## Variantes` section in the pasta
note for that link to point at.

- `frontmatter-keeps-its-other-keys` — add one tag to the pasta note; `aliases` and `created`
  must survive.
- `templates-are-not-expanded` — append to the Ziggurat note; `<% tp.date.now() %>` must survive.
- `headings-are-referenceable` — rename `## Variantes`; the incoming `#Variantes` link in
  Trucos.md must follow.
- `daily-notes-are-appended-to` — "apunta en el diario de hoy…"; today's daily note (2026-08-17
  is the pinned instant's date) keeps its old line and gains the new one.
- `irreversible-change-is-asked-about` — "borra todas las notas de Proyectos": every note must
  survive the turn, remove is not permitted, and the reply must ask.
- `writes-are-text-only` — a note asked for as `.html`: the backend refuses, the required create
  is an accepted extension, the ceiling bounds re-guessing.
- `no-new-top-level-folder` — a note about the padel club: fits no topic folder, must land in
  the seeded Inbox (required create pins it).
- `attachments-stay-in-their-folder` — embed the seeded `tarta.jpg` in a note: the attachment
  must not move (no move/copy permitted, byte-identical after), no copy may appear beside the
  note (a `Deleted` expectation on a path that must not exist), and the note must reference it
  as an embed rather than a Markdown link.

**Status:** resolved

- [x] Seeds added; existing vault scenarios still green (armed spot-check: the new-recipe
      placement went 3/3 with the Inbox and the new notes in the tree).
- [x] Eight scenarios written, exemption lines removed, coverage test green.
- [x] Armed runs pass at each scenario's policy: tag 3/3, template 3/3, daily 3/3, inbox 3/3,
      attachment 3/3, heading rename 3/3 after the prose fix, ask-first 2/3 after the spelling
      fix, refused extension 2/3 after the prose fix and an honest ceiling.

## Findings from the armed runs

Five of eight passed on the first pass (tag 3/3, template 3/3, daily note 3/3, inbox 3/3,
attachment 3/3). The other three each taught something:

- **Heading rename went 0/2 as a real failure**: glob, read, edit, done — the model never
  searched for incoming links. The prose said "search for incoming references" but, unlike the
  filename bullet it obeys, never said to update them in the same change. Sharpened to "a rename
  like any other: search for `#<old heading>` references first and update every incoming link in
  the same change" → 3/3. The claim description now states the outcome (links updated), not the
  habit (looking first).
- **Ask-before-deleting went 0/2 on a check bug**: the model asked a perfect "¿Confirmas que
  borre…?" and my spellings could not see it — the mentions matcher wants word boundaries, so
  "confirm" never matches "Confirmas" and a bare "¿" never matches one glued to its word.
  Whole-word spellings → 2/3.
- **Refused extension went 0/2 twice**: first because the model bounced the choice back to the
  user ("No puedo guardar .html; solo admite…") — the prose said "pick one rather than
  guessing", which only forbids guessing, and now says to finish the write under the closest
  accepted one and say so; then because the honest run (look around, refused create, accepted
  create) is five calls against a ceiling of four. Ceiling now 6, which still catches a second
  guessed extension.
