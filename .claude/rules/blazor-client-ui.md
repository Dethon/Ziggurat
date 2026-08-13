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

Every icon is inline SVG. A component never spells one out: the geometry lives in that app's
`Components/Icons.cs` as an `IconGlyph`, and `Components/Icon.razor` puts the wrapper round it —
the viewBox, the fill-or-stroke decision, `aria-hidden`. A call site names a glyph and sizes it:

```razor
<Icon Glyph="Icons.Archive" Size="18" />
<Icon Glyph="Icons.Sync" Size="14" Class="status-icon" />
<Icon Glyph="Icons.ChatBubble" />   @* no Size: CSS decides *@
```

The label belongs to the control (`aria-label` or `title`), never to the icon. Adding an icon means
adding one `IconGlyph` and naming it — never pasting an `<svg>` into a component, and never
inventing a second wrapper.

WebChat's set has two families that are not interchangeable: filled glyphs on a 24-unit grid, and
stroked ones carrying the width they were drawn at. Mixing them inside one control reads as a
rendering bug rather than a choice. Three composer icons keep the viewBoxes they were drawn at,
because rescaling path data by hand is a transcription error waiting to happen.

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

`Tests/Unit/DrawnIconConformanceTests.cs` holds both halves of this. One test walks the markup of
both clients and fails on any code point above U+2190 except `→`, plus `×`; its one exemption is
`SuggestionChips.razor`, whose emoji are content a person reads rather than controls. The other
fails on any component that spells an `<svg>` itself, exempting only the two `Icon.razor` renderers.
Adding an exemption to either is a decision to argue for, not a way to get the build green.

The two clients keep separate icon sets on purpose. Sharing them would need a Razor class library
that both reference, which is more machinery than two small files are worth; the duplication is
`IconGlyph` and a wrapper, not the geometry.

## State a toggle by turning one icon

Two glyphs swapped by a ternary is how the collapsible toggles were written, and it left the
`transition: transform` on `.toggle-icon` doing nothing for as long as it existed. One icon plus a
CSS class that rotates it animates for free and halves the markup. `.toggle-icon` /
`.toggle-icon.open` is the pattern.

## One arrow, not seven

`Dashboard.Client/Components/SortArrow.razor` draws the sortable-column arrow for every page. Seven
pages each carried their own copy of the same glyph pair before it existed, which made the arrow
seven decisions that happened to agree. A header writes
`<SortArrow Column="Time" SortColumn="@_sortColumn" Ascending="@_sortAsc" />` and no page defines a
`SortIndicator` of its own.
