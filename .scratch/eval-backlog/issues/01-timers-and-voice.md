# 01 — Timers and voice: the cheap authoring batch

Seven claims, all closable against fixtures that already exist.

- `timers.listed-by-glob` + `voice.several-sentences-only-when-asked` share one scenario: two
  armed timers, "¿qué temporizadores tengo puestos y cuánto les queda?" — a list question, so
  several sentences are allowed and bounded at three; the glob is the required call and the two
  remaining times (5 and 50 minutes, distinct on a word boundary) must survive into the reply.
- `timers.cancelled-by-removing-it` — "quita el temporizador de la pasta" with the timer armed;
  the required call is a remove of `/timers/pasta`.
- `timers.text-is-spoken-never-an-instruction` — a reminder that forces a timer and asserts the
  message landed in `text`: create with `text` carrying the errand. The never-an-instruction half
  is the mechanism discrimination the guard scenarios already bound; what this cites is the
  message-goes-in-text half.
- `voice.unclear-request-is-acted-on` — "cinco minutos para el té": no verb, likeliest reading is
  a timer, required create of 300 seconds; a model that asks instead never creates and fails.
  The ask-before-destroying half lives in the vault issue (`irreversible-change-is-asked-about`).
- `voice.abbreviations-are-spelled-out` — "¿a qué temperatura está puesto el aire del salón?":
  the answer is a number with a unit; the reply must carry "grados" and never a degree symbol.
- `voice.one-word-before-slow-work` — the recording *does* see the acknowledgement: the reply
  string concatenates the pre-tool text (which is why `ReplyChecks.WithoutAcknowledgement`
  exists). New `ReplyExpectation.AcknowledgesFirst` check: the first sentence is exactly one
  word. Carried by a spoken research turn (the raffle total by voice — search plus two fetches
  is unambiguously slow work).

**Status:** resolved

- [x] `AcknowledgesFirst` check in ReplyChecks, proven against fixed strings.
- [x] Six scenarios written, exemption lines removed, coverage test green.
- [x] Armed runs pass at each scenario's policy.

## Answer

All six landed on the first armed pass, every run green: the glob listing 3/3 (both remaining
times survived at a word boundary — 5 and 50 minutes), cancel 3/3, the errand-in-text 3/3, the
tea timer 3/3, the spoken temperature 3/3, and the acknowledgement 4/4 — which also settles the
old needs-fixture reason: the recording *does* see the pre-tool word, concatenated ahead of the
answer in the reply string, exactly where `WithoutAcknowledgement` was already stripping it.
`AcknowledgesFirst` is the new ReplyExpectation flag; it demands a one-word first sentence with
an answer behind it, and only scenarios whose work is unambiguously slow may declare it.
