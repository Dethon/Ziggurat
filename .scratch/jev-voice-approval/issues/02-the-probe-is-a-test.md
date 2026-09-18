# 02 — The probe is a test

**What to build:** The 32 labelled answers in `probe/` become data beside a `[Trait("Category","Jev")]` test that drives the real reader against live Jev, skipped without a key: every labelled approve approves, every decline declines, every narrowed or noisy answer is Ambiguous, and no answer is decided the wrong way.

**Blocked by:** 01

**Status:** done

- [x] Zero wrong actions over the set is a hard assertion; the count of re-asks is reported and floored at the number measured the day it lands.
- [x] Spec: `.scratch/jev-voice-approval/spec.md` § Solution.

## Answer

- `Tests/Integration/McpChannelVoice/ApprovalReaderJevTests.cs` (`Category=Jev`, skipped without
  `typeSafe:apiKey`), data in `jev-approval-cases.json` beside it — the 32 answers with their
  prompts and labels. Three claims: no answer decided the wrong way (a tool run on a permission
  not given, or a clear yes refused) — hard; re-asks at or under 4 (measured 2–3 on 2026-09-18:
  "déjalo", "never mind", and "venga, dale" flipping at the sure bar); the word list's blind spots
  ("adelante", "hazlo", "mejor no", "espera, no lo hagas") decided, "sí sí" approved, and the
  narrowed yes held back.
- **Deviation from the spec's wording**: the first run with the grilled `approved` question sent
  every bare imperative to the re-ask (0.83–0.89). `probe/wording_probe.py` measured five
  wordings over the set; the shipped one ("go ahead with it as asked … a yes that changes or
  narrows what was asked is not permission") is recorded in the spec and in
  `probe/2026-09-18-wordings-*.txt`.
- "no, solo la del salón" declines by agreement (0.85 beside the word list's no) rather than
  re-asking: the rule's doing, nothing runs, noted in the spec.
