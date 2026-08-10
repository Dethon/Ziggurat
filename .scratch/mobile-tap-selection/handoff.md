# Mobile "double tap to select a conversation"

Status: resolved
Date: 2026-08-10
Branch: `fixes-and-message-types`
Fixed in: `80d2b0d1`, confirmed one-tap on the reporter's Android Chrome PWA

## Resolution

Flicking the hearth sheet open leaves Chromium's input pipeline in a state where the next tap
arrives as `pointerdown → touchstart → pointerup → touchend` with **no `mousedown`, `mouseup` or
`click` at all**, for roughly a second. Blazor's `@onclick` on `.topic-item` never fires, so the
tap does nothing. The tap after it gets the full chain and selects normally.

The cure is to consume the drag's own `touchmove` stream. `_onMove` already called
`preventDefault()`, but on a **pointer** event, and that does not reach the decision Chromium
makes from the touch stream. Nothing anywhere consumed the touchmoves of a sheet-chrome drag:
`register` only attached a non-passive `touchmove` listener to `.hearth-rows`, and `_onDown`
bails for rows targets, so a handle drag never reached `_onRowsTouchMove`.

Fix in `WebChat.Client/wwwroot/app.js`:

- `register` also binds `touchmove` on the sheet, non-passive.
- `_onSheetTouchMove` calls `preventDefault()` while `_dragging`.

Gated on `_dragging`, not on the axis lock: the lock only engages 8px in, and the moves before it
belong to the same gesture. `_onDown` leaves `_dragging` false for touches inside `.hearth-rows`,
which is what keeps the conversation list's native scrolling intact.

## Why four earlier fixes did nothing

Every one of them (`e7d6496c`, `f34a91f0`, `a0221805`, `77c4b738`) addressed the dropdown dismiss
backdrop. The reporter later confirmed the bug reproduces **with no dropdown ever opened since
launch**, on a rebuilt and force-reloaded PWA. That whole family was never the cause.

They also could not have been caught: every "gesture" test in the suite builds its drag from
`new PointerEvent(...)` inside the page (`HearthNavigationE2ETests.cs:186-216, :238-257`).
Untrusted events call the app's handlers but never enter Chromium's gesture recogniser, so they
can produce neither the failure nor a compatibility click. **A gesture test that does not go
through CDP `Input.dispatchTouchEvent` is not testing the gesture.**

## Reporter's device facts (the ones that cracked it)

Android phone, PWA installed via Chrome, standalone. Blink — the same engine as the E2E suite.

1. The conversation list **does** finger-scroll. Kills the old top hypothesis that
   `touch-action: none` on `.hearth` blocks it. Effective `touch-action` is intersected only up
   to the first containing scroll container, which is `.hearth-rows` itself; `.hearth` sits above
   it and is excluded. The comment at `app.css:2521` claiming otherwise is wrong.
2. The soft keyboard is never involved.
3. Reproduces with no dropdown ever opened.
4. Rebuilt and force-reloaded after `77c4b738`.
5. **The discriminator**: half detent fine, slow slide to full fine, **flick** to full broken.
6. Nothing at all happens on the dead tap; search box and `+` are dead too, not just rows.
7. ~0.5s after the flick still dead, several seconds fine. Either time or one sacrificial tap
   ends it. The "first tap anywhere" was buying time, not clearing state.

## Reproduction

`Tests/E2E/WebChat/HearthFlickE2ETests.cs`. Three tests, all green after the fix; the flick one
was red before it, verified by hand both ways.

- flick to full then tap → the bug. 300px over 8 CDP touchMove steps, no gap; app `_vy ≈ -2.2`
  px/ms against its own `-0.6` threshold, asserted rather than assumed.
- slow slide to full then tap → control, same destination, `_vy ≈ -0.3`. Green throughout.
- drag inside `.hearth-rows` still scrolls it → guards the fix's only regression risk. Verified
  non-vacuous: dropping the `_dragging` gate turns it red.

```
cd /home/dethon/repos/ziggurat && PLAYWRIGHT_HEADLESS=true \
  dotnet test Tests --filter "FullyQualifiedName~HearthFlick|FullyQualifiedName~HearthNavigation"
```
13 passed, ~1m50s.

## Ruled out by bisect, not by argument

- **`_swallowTrailingClick`** — neutered entirely, still failed. It had also self-disarmed 4ms
  before the tap.
- **Hit-test lag behind the transform transition** — `transition: none`, still failed. The sheet
  was fully settled at the dead tap (`rect=0,68,390,776`, `matrix(1,0,0,1,0,0)`) and
  `elementFromPoint` returned the aimed row exactly.
- **Stale gesture state** — every field rewritten to pre-gesture values, still failed.
  `pointercancel` never fires. The reporter also confirmed dragging outside the drawer after a
  flick moves nothing, so `_dragging` is not left true.
- **`touch-action: none`** — set to `auto`, still failed.
- **Falsy-zero `parseFloat(...) || (base - 64)`** at `app.js:437` — branch never taken.
- **Stale PWA build** — rebuilt and force-reloaded; still reproduces.

## Still open

Why Chromium enters that state at all. The signature matches post-fling tap suppression, but
nothing scrollable is under the gesture and no scroll was ever observed in any run. This does not
affect the fix, which is validated end to end, but nobody should claim the Blink mechanism by
name without observing it.

## Known-cosmetic issues found and deliberately not fixed

Both confirmed inert for this bug; separate hygiene if anyone cares.

- `_axisLocked` is never reset in `_onUp` (`app.js:402-416`).
- `parseFloat(getComputedStyle(...).getPropertyValue('--sheet-offset')) || (base - 64)` at
  `app.js:437` treats a computed offset of exactly 0 as falsy and silently substitutes the peek
  offset.
- The `app.css:2521` comment about `touch-action` on the sheet is wrong, as above.

The five throwaway bisect suites written to reach those verdicts were deleted rather than kept.
They lived outside `Tests/`, so they never compiled, and their machinery is superseded by
`HearthFlickE2ETests.cs`. Recover them from this branch's workflow transcripts if a verdict above
is ever doubted.
