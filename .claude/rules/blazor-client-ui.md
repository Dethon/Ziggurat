---
paths:
  - "WebChat.Client/**"
  - "Dashboard.Client/**"
  - "Tests/Unit/WebChat.Client/**"
  - "Tests/Unit/Dashboard.Client/**"
  - "Tests/E2E/WebChat/**"
  - "Tests/E2E/Dashboard/**"
---

# Blazor client UI

Both clients — WebChat and the dashboard — follow this.

## An icon is drawn, never typed

Every icon is inline SVG, on one shape: `viewBox="0 0 24 24"`, `fill="currentColor"`, explicit
`width`/`height` sized to the control, `aria-hidden="true"`, and the label carried by the control's
`aria-label` or `title`. `ChatInput.razor` holds the reference set — send, stop, attach, microphone.
The dashboard sidebar sizes in `em` instead, so an icon follows the text around it. Copy one of
those rather than inventing a third shape.

An emoji or a dingbat pasted into markup is not a smaller version of this. The platform's font
decides what the glyph looks like, so it lands at a different weight and often in full colour
beside the SVG icons it sits with, and it changes between the desktop the change was made on and
the phone the app is actually used from. That includes the ones that look typographic rather than
pictorial: `&times;`, `&#10003;`, `▾`, `▼`, `⚙`, `○`, `⟳`, `◉`, `◆`, `⚠` — every one of those was
in this codebase and every one was wrong for the same reason. If you find yourself reaching for a
text-presentation variation selector to stop a glyph rendering as an emoji, that is the argument
against typing it, not a fix.

Prose is not an icon. A `→` inside a label ("speech end → audio") is punctuation and stays. So does
an em dash standing in for an absent value.

`Tests/Unit/DrawnIconConformanceTests.cs` walks the markup of both clients and fails on any code
point above U+2190 except `→`, plus `×`. It has one exemption, `SuggestionChips.razor`, whose emoji
are content a person reads rather than controls. Adding a second exemption is a decision to argue
for, not a way to get the build green.

## State a toggle by turning one icon

Two glyphs swapped by a ternary is how the collapsible toggles were written, and it left the
`transition: transform` on `.toggle-icon` doing nothing for as long as it existed. One icon plus a
CSS class that rotates it animates for free and halves the markup. `.toggle-icon` /
`.toggle-icon.open` is the pattern.

## One arrow, not seven

`Dashboard.Client/Components/SortArrow.cs` draws the sortable-column arrow for every page. Seven
pages each carried their own copy of the same glyph pair before it existed, which made the arrow
seven decisions that happened to agree. A page's `SortIndicator` delegates and nothing else.
