# Jev voice approval

Status: ready-for-agent

Grilled 2026-09-18. Fifth of the five Jev uses (`.scratch/jev-skill-preload/spec.md` names the
order); reuses that spec's contract and client. Rules: `.claude/rules/voice.md`. Probe:
`probe/2026-09-18-answers.txt` (the first block is the skill-preload probe's approval half).

## Problem Statement

When the agent needs permission mid-turn on a satellite, it asks "¿Apruebas X? Di sí o no." and
reads the answer by two word lists. "Adelante", "hazlo", "mejor no" and "espera" are all
*ambiguous* to those lists, so the person hears "No entendí" and the question again. Worse, the
lists read a sentence as a bag of words: "sí, pero la de la cocina no" holds a yes and no no, and
is approved — a tool runs on a permission the person narrowed.

## Solution

Every captured answer goes to Jev with the spoken prompt, and two yes/no questions are asked:
did the person give permission to do exactly what was asked, all of it; did they refuse it or
tell the assistant not to do it as asked. Jev alone acts when it is sure (0.9 one way, 0.1 the
other). When it only leans (0.5 / 0.1) and the word list leans the same way, the two act
together — "sí sí" approves. Anything else re-asks once, as today. When Jev is late or down the
word list decides, as today.

Measured 2026-09-18 on 32 spoken answers in Spanish and English: no wrong action at the sure
bars; "adelante", "hazlo", "venga, dale", "mejor no", "ni se te ocurra", "espera, no lo hagas"
all decided; both narrowed answers sent to the re-ask; "vale", "déjalo", "never mind", "sí sí"
and "sí... bueno, vale" under the sure bar, the first and the last two rescued by agreement.
About 330 ms warm.

## User Stories

1. As a person, I want "adelante" or "hazlo" to count as yes and "mejor no" as no, so that I answer the way I speak and am not asked twice.
2. As a person, I want "sí, pero la de la cocina no" never to run the whole thing, so that a narrowed permission is asked about again rather than taken as given.
3. As a person, I want a plain "sí sí" to approve at once, so that Jev's caution never makes the assistant slower to believe me than it is today.
4. As a person, I want approval to work when TypeSafe is down exactly as it does today.
5. As a person, I want whisper's filler ("thank you.", a subtitle credit) never read as an answer, so that noise cannot approve a tool.
6. As the maintainer, I want every bar in the voice server's `appsettings.json`, and the metric to say who decided, so that the bars can be re-read from production.

## Decisions

- **Where.** `RequestApprovalTool`, in place of the bare `ApprovalGrammarParser.Parse` call, behind
  an `IApprovalReader` the tool takes from DI so the test drives it with a fake. The parser stays
  as the fallback and the agreement partner.
- **State.** `{ "prompt": <what was spoken>, "answer": <the transcript> }`. Nothing else.
- **Questions.** `approved`: "`prompt` was spoken aloud asking for a yes or no about doing exactly
  what it names. `answer` is the transcribed spoken reply. Did the person give permission to go
  ahead with it as asked? A yes that changes or narrows what was asked is not permission."
  `declined`: same preamble, "Did the person refuse it, or tell the assistant not to do it as
  asked?"
  *Changed at implementation (2026-09-18, `probe/2026-09-18-wordings-*.txt`):* the grilled
  wording asked for permission "to do exactly that, all of it, without changing or narrowing it",
  which the probe had measured only on the eight conditional answers; over the first block Jev
  read a bare "adelante", "hazlo", "go ahead", "do it" as not quite that (0.83–0.89), so every
  answer the word list is blind to went to the re-ask. The shipped wording keeps them at 0.91+
  and both narrowed answers out of the approval. Under the rule "no, solo la del salón" declines
  by agreement rather than re-asking — nothing runs either way.
- **Rule.** With `a` = approved, `d` = declined, `w` = the word list's verdict:
  1. `a ≥ sure` and `d ≤ counter` → approved; `d ≥ sure` and `a ≤ counter` → rejected
     (`sure` 0.9, `counter` 0.1).
  2. else `a ≥ lean` and `d ≤ counter` and `w` = Approved → approved; `d ≥ lean` and `a ≤ counter`
     and `w` = Declined → rejected (`lean` 0.5).
  3. else ambiguous → re-ask once, then rejected, as today.
  No answer from Jev (deadline `approval.judgment.deadlineMs` 1000, error, 429/529, empty key) →
  `w` alone, exactly as today.
- **Host.** `McpChannelVoice` registers the Infrastructure client — it already takes Infrastructure
  for the transcription client, and this is the same kind of reason. `typeSafe` settings in its
  `appsettings.json`, key from `${TYPESAFE_API_KEY}` on its compose service.
- **Telemetry.** `VoiceMetric.ApprovalResolved` keeps its `Outcome` and gains who decided
  (`judgment`, `agreement`, `wordlist`) and the two probabilities; the latency of the call rides
  on the same event.

## Out of Scope

- Changing the prompt's wording or the re-ask count.
- Reading anything but the answer to this prompt — no conversation, no tool arguments.
- Replacing the word list. It is the fallback and must keep its tests.
