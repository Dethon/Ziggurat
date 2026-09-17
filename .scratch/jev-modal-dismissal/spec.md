# Jev modal dismissal

Status: ready-for-agent

Grilled 2026-09-18. Fourth of the five Jev uses (`.scratch/jev-skill-preload/spec.md` names the
order); reuses that spec's contract and client. Rules: `.claude/rules/web-browsing.md`. Probe:
`probe/README.md`.

## Problem Statement

Every navigation in the browser runs `ModalDismisser`: one round trip finds a visible overlay by
the names in its class or id, then the overlay's controls are probed by selector and by a word
list per kind (`accept`, `agree`, `close`, `no gracias`, `ahora no`…). A wall whose button says
"Entendido", "Continuar sin aceptar" or "Quizás más tarde" is detected and left standing. The
cost is small — extraction strips overlays, and the model can see the buttons and click one — but
it is a round trip the model pays, and on a cookie wall the word lists can only ever say yes: a
"Rechazar todo" beside an "Aceptar todo" is never the one chosen.

## Solution

When an overlay is detected and none of the existing paths matched a control, its buttons and
links go to Jev by accessible name, and Jev picks the one that does what that kind of overlay
wants closed with — or none. Cookie walls are asked two questions, *reject* and *accept*, and the
code takes a confident reject first: the wall closes with the fewest cookies where the page
offers it. Age gates are entered, newsletters and sign-ups declined, notification prompts denied.
A pick is clicked only above a confidence bar; anything else, including a late or absent answer,
leaves the page exactly as today. The selector and word-list paths stay first and cost nothing.

## User Stories

1. As a person, I want a page whose consent wall says "Entendido" read at once, so that the agent does not spend a turn clicking what the dismisser could have.
2. As a person, I want "Rechazar todo" clicked when a wall offers it beside "Aceptar todo", so that browsing leaves the fewest trackers in the browser's persisted cookies.
3. As a person, I want a wall that offers only "Aceptar y continuar" still closed, so that fewest-cookies never means left standing.
4. As a person, I want "Configurar" or "Manage preferences" never clicked, so that the dismisser never opens a settings panel in place of the wall.
5. As a person, I want a newsletter popup declined rather than subscribed and a notification prompt denied, so that a dismissal never signs me up for anything.
6. As a person, I want an overlay whose controls are a page's own navigation left alone, so that "Iniciar sesión" is never clicked because it sat inside something that looked like a popup.
7. As a person, I want a page to load in the same time it does today when there is no overlay, so that Jev costs nothing on the common page.
8. As a person, I want browsing to work when TypeSafe is down exactly as it does today.
9. As the maintainer, I want a dismissal outcome per navigation — none detected, by selector, by text, by judgment, left standing — so that the miss rate is a number before and after.
10. As the maintainer, I want the probe kept as a test, so that a Jev bump or a question edit is checked in a minute.

## Decisions

- **Trigger.** In `ModalDismisser`, per detected overlay, after the selector path and the text path
  both returned nothing. Never for an overlay the container names did not detect; that is out of
  scope.
- **State.** `{ overlay_kind, controls: [{ index, role, name }] }` — the overlay's visible
  buttons and links by the accessible-name approximation the text path already computes, capped
  at `judgment.maxControls` (20), in document order. No url, no page text.
- **Questions.** Choice over the controls by index plus `none`. Cookie: `reject` ("closes the wall
  refusing all optional cookies or keeping only the necessary ones") and `accept` ("closes the
  wall accepting or acknowledging the cookies"), each stating that a control which opens settings
  or more information does not close the wall. Age: "confirms the person is an adult and enters
  the site". Newsletter: "closes or declines the popup without subscribing". Notification:
  "declines or dismisses the request to send notifications". Exact wording in `probe/README.md`.
- **Rule.** Cookie: the `reject` pick if its confidence ≥ `judgment.confidence` (0.6), else the
  `accept` pick at the same bar, else nothing. Other kinds: the one pick at the bar. `none` is
  never clicked.
- **Budget.** The call runs inside the existing dismissal, after detection, under
  `judgment.deadlineMs` (1000) — paid only by a page that would otherwise keep its overlay.
  Absence, deadline, 429/529: no click, outcome `left-standing`.
- **Click.** By the control's index into the same locator list the names came from; a stale or
  refused click is the next-best nothing, as the text path already treats it. The result is a
  `ModalDismissed` with selector `judgment(<index>)` and the control's name.
- **Host.** `McpServerWebSearch` registers the Infrastructure client (it already references
  Infrastructure for the browser). Settings `typeSafe: { apiUrl, apiKey, model }` in its
  `appsettings.json`, the key from `${TYPESAFE_API_KEY}` on its compose service; `judgment`
  settings beside them. An empty key is the feature off.
- **Telemetry.** A `ModalDismissalEvent` per navigation with an overlay: kind, outcome (`selector`,
  `text`, `judgment`, `left-standing`), and for a judgment the pick, its confidence and latency.
  Charted on the dashboard's web page beside the browse metrics.

## Out of Scope

- Detecting overlays the container names miss (a generic fixed-position detector).
- Replacing the selector or text paths.
- Any use of Jev on page content, search results or the model's choice of what to read.
