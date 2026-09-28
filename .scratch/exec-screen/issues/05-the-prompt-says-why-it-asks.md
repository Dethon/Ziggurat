# 05 — The prompt says why it asks

Status: resolved

Spec: `../spec.md` (Decisions: The reason travels as codes). Blocked by: 04.

Render `ToolApprovalRequest.Screen` on each channel, in that channel's language:

- **Telegram:** one line above the inline keyboard.
- **WebChat:** one line on the approval card, drawn as text and never as an emoji or dingbat.
- **Voice:** one short sentence spoken **before** the unchanged "¿Apruebas …? Di sí o no.".
  `IApprovalReader` is still handed the question alone. The re-ask repeats the question and not
  the reason.

A request with no `Screen` renders exactly as today.

Tests per channel: each code renders, and absence is unchanged. For voice, the reader receives
the measured question text.

Update `.claude/rules/channels.md` and `.claude/rules/voice.md` to say why the reason stays
outside the question the reader reads.
