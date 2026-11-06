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

## The service worker answers only the app's own pages

WebChat's published service worker answers a navigation with the cached `index.html`, which is what
lets an installed app open offline. Some pages on the same host are not the app's — sign-in and a
link's enrollment (`/accounts/*`, the accounts service) and a login handoff and the logins page
(`/browse/*`) — and the proxy routes them past it. `otherServicesPages` in
`service-worker.published.js` lists them so their navigations reach the network; a page added under
another service needs its path there, or an installed app hands the person the chat instead. The
signed-out E2E (`ASignedOutBrowser_SignsInAndComesBackAsThemselves`) waits for the worker to control
the page before signing out, which is the case that loops without it.

A worker from before that list (master's) answered those pages with the app too, so once a new
worker is active it sends every open `/accounts/*` page on to the network (`reopenAccountsPages`):
the passkey link the upgrade's migration prints, opened where the old app was installed, reaches
enrollment without a reload
(`AnEnrollmentLinkAnOldWorkerAnswered_ReachesEnrollmentOnceTheNewWorkerTakesOver`). Outside
activation's `waitUntil`, retried until it takes: only a page's active worker may navigate it, and
inside `waitUntil` the worker is still activating. The price, once per deploy, is a sign-in or
enrollment page open at that moment reloading under the person, a passkey prompt with it. Never
`/browse/*`: a login handoff's page is a single-use link, and reloading it would spend it.

Tests of a worker update cannot use a route on the context that updates: an intercepted worker
script is never byte-compared, so every update check reinstalls the worker (and re-runs its
activation), and `CreatePageAsync`'s catch-all route arms interception on every E2E context. That
test installs the old worker on a profile of its own (`OpenProfileAsync`) and opens it again with
nothing routed. The E2E stack's Caddyfile mirrors the deployment's `@shell` route, without which a
signed-out browser can never fetch the new worker at all.

## A message is Markdown, never markup

WebChat draws a message through `ChatMarkdown` (`WebChat.Client/Helpers`) alone: Markdig with raw HTML
disabled, and a link or image whose destination names a scheme other than http(s), mailto or tel
replaced by its own words. A message is whoever wrote it — another member of a shared topic, or a
model a page or a household server talked into echoing something — and the page is same-origin with
the admin API, so markup in it ran as the reader (pass 18: a member's `<img onerror>` in a shared topic
ran in the owner's page). Never render message text as a `MarkupString` any other way
(`ChatMarkdownTests`, `WebChatE2ETests.AMessageCarryingMarkup_IsShownAsText_AndRunsNothing`).
