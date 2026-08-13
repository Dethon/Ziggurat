---
paths:
  - "WebChat.Client/**"
  - "Tests/Unit/WebChat.Client/**"
  - "Tests/E2E/WebChat/**"
---

# WebChat UI

## An icon is drawn, never typed

Every icon in this app is inline SVG, on one shape: `viewBox="0 0 24 24"`, `fill="currentColor"`,
explicit `width`/`height` sized to the control, `aria-hidden="true"`, and the label carried by the
button's `aria-label` or `title`. `ChatInput.razor` holds the reference set — send, stop, attach,
microphone. Copy that shape rather than inventing a second one.

An emoji or a dingbat pasted into markup is not a smaller version of this. The platform's font
decides what the glyph looks like, so it lands at a different weight and often in full colour
beside the SVG icons it sits with, and it changes between the desktop the change was made on and
the phone the app is actually used from. That includes the ones that look typographic rather than
pictorial: `&times;`, `&#10003;`, `▾`, `▼`, `⚙`, `○`, `⟳` — every one of those was in this codebase
and every one of them was wrong for the same reason.

`Tests/Unit/WebChat.Client/DrawnIconConformanceTests.cs` walks the markup and fails on any code
point above U+2190, plus `×`. It has one exemption, `SuggestionChips.razor`, whose emoji are
content a person reads rather than controls. Adding a second exemption is a decision to argue for,
not a way to get the build green.

## State a toggle by turning one icon

Two glyphs swapped by a ternary is how the collapsible toggles were written, and it left the
`transition: transform` on `.toggle-icon` doing nothing for as long as it existed. One icon plus a
CSS class that rotates it animates for free and halves the markup. `.toggle-icon` / `.toggle-icon.open`
is the pattern.
