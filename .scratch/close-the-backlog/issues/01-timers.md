# Timers: listing, cancelling, and what goes in text

Status: resolved

## Answer

All three scenarios armed-validated on 2026-08-19, 3/3 each:

- "the running timers are listed by globbing" — glob required, both timers named in a spoken
  reply of at most three sentences; cites `timers.listed-by-glob` and
  `voice.several-sentences-only-when-asked`.
- "a timer is cancelled by removing its directory" — remove at `/timers/pasta`; cites
  `timers.cancelled-by-removing-it`.
- `RemindMeInTenMinutes` now carries a second judged check on the created timer's `text`
  field; the judge graded it on every run (`timers.text-is-spoken-never-an-instruction`
  3/3, coverage `judged`).

Three timer claims have no witness, and one voice claim rides naturally on the first of them.

- `timers.listed-by-glob` — a scenario whose subject is listing: two timers armed, "¿qué
  temporizadores tengo puestos?", the glob required, both names in the reply.
- `voice.several-sentences-only-when-asked` — the same reply: the user asked for a list, so up
  to three sentences are allowed and every item must be read. Cited here because every other
  spoken scenario caps at one sentence and none asks for a list.
- `timers.cancelled-by-removing-it` — "quita el temporizador de la pasta": the remove required
  at /timers/pasta, nothing else written.
- `timers.text-is-spoken-never-an-instruction` — a judgement about a sentence: whether the
  created timer's `text` reads as words spoken to a person or as an instruction to be carried
  out. Rides `RemindMeInTenMinutes` as a second judged check, beside the descriptive-id one,
  because that is the scenario whose create call carries a text worth judging.
