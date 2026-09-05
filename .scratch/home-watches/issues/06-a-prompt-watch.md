# 06 — A prompt watch

**What to build:** Asked "warn me when Laura's sugar goes above 180" on Telegram, the agent writes a watch whose effect is a `prompt`, delivered to Telegram; asked the same in the office, it delivers to the office satellite. The rendered automation calls the watch-fired rest_command with the watch id and name, the rendered prompt, and the firing facts from the trigger variables (entity id, friendly name, from-state, to-state, the trigger's description, the fire instant). The guide teaches the default — a plain "warn me" is a prompt watch delivered where it was asked from, an insistent announcement is added only for urgency — a range as two triggers, that a numeric threshold fires on the crossing only so the current value is worth saying at creation, `for` on noisy sensors, that the agent reads back what it created, and that Nabu cannot deliver to Telegram.

**Blocked by:** 03 — A home-action watch; 05 — The home can raise a prompt with the agent.

**Status:** done

- [x] Over the fake client: a `prompt` effect renders the watch-fired call with every firing fact as a template; `deliverTo` and `userId` land in the automation's description and round-trip through `watch.json`; a missing `deliverTo` reads back as absent and the callback applies the configured default.
- [x] The guide's section and claims cover the default effect, ranges, crossing-only semantics, read-back and Nabu's channels; prompt snapshot and claim coverage pass.
- [x] Eval scenarios: warn on Telegram (deliverTo telegram), warn on voice (deliverTo the speaking satellite), a range (two triggers, one watch), urgent (announce then prompt), a threshold change (same id, watch count unchanged), removal (count down); each declares its change and its claims.
- [x] Spec: `.scratch/home-watches/spec.md` § Rendering the automation, § Prompts and discovery.

## Comments

- 2026-09-05: a file naming no `deliverTo` does not read back as absent — the mount fills it with
  the caller's own channel and address at write time, because the model is never told which
  channel a turn came from (only voice decorates the turn). The callback still applies the shared
  default (`Domain/delivery.json`) to a payload that names none.
