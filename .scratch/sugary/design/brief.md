# Sugary redesign: audit and direction

Status: direction agreed for building; slices below are the plan. The shared reference for the
builder and every reviewer of the redesign.

The ask, verbatim: "I want to change the ui and ux design. first of all I'd like the theme to be
cake inspired. a bit off a joke with the theme and the name. also the ux should be easier for the
user to use and navigate. iterate on that autonomously, enfasis on quality, I expect professional
level work".

Who it is for: one woman with type 1 diabetes, in her thirties, in Spain, English or Spanish UI.
She photographs or films a meal, types a few words, scans a barcode or a label, or picks one of
her saved foods; she gets every food with a carb range and a few tap-to-answer questions; she saves
the meal, and later records whether her glucose ended in range. **She decides insulin from these
numbers**, often at a restaurant table, often one-handed. Everything below serves that moment
first; the cake theme is the voice and the paint, never the numbers.

## 0. How the screenshots are made

A Playwright harness builds `Sugary.Client` into a throwaway folder (never into
`McpServerSugary/wwwroot`), serves it with `vite preview`, mocks every `/api` call in the browser
(anything unmocked is aborted and reported, so the real server is never reached and no model is
called), and captures 31 states at 390×844 @2x in en/es × light/dark: sign-in (and wrong code),
meal empty, with photos, camera (photo, ready to film, filming), with a clip, estimating, estimate,
answers picked, after answers, editing a portion, foods sheet, food amount, estimate with an
already-counted food, packaged sheet, live scanner, packaged result, manual entry, estimate error,
save bar, saved toast, log, meal sheet, outcome picked, settings, my foods, food edit, estimates
unavailable, empty log. Scrolling screens also get a `-full.png`; sheets that scroll get a
`-bottom.png`. Each variant also gets `targets.txt`: every visible control under 44 px.

- Harness: `/tmp/claude-1000/-home-dethon-repos-ziggurat/e9ef3238-1d80-4fc5-8797-024abcc7add3/scratchpad/design/harness/`
  (`run.sh`, `capture.mjs`, `fixtures.mjs`, `fixtures/`; `tools/` beside it). Run: `…/harness/run.sh <name>` → PNGs
  in `…/design/<name>/<lang>-<scheme>/`. Filters: `VARIANTS=en-light`, `STATES='estimate|log'`,
  `NO_BUILD=1`. Latest full pass: `…/design/final/`. The first harness (redesign rounds, `before/`)
  is at `/tmp/claude-1000/-home-dethon-repos-sugary/46283ba1-5e7d-46db-9a5f-e9750b6856c6/scratchpad/design/`;
  it predates doses, exercise and the cycle and hangs on the Log's lazy images.
- Since 2026-10-17 it also mocks `/api/doses`, `/api/exercise` (and `/names`), `/api/periods` and
  `/api/day-notes` (`fixtures.mjs`: `doses`, `exercise`, `periods`, `dayNotes`; meals carry their
  linked `doses`, and every fixture time hangs off one `NOW`, so a dose saved with its meal shares the
  meal's instant as the server makes it). States 33–40c: the Log with doses and exercise, the dose
  sheet new and opened, another exercise, a past one, a running session and its sheet, a meal's sheet
  with its insulin and a dose added from it, the Cycle tab, a day, a period, an empty cycle. The iPhone
  keyboard states moved to 50/51. Waiting for images is capped at 2.5 s (lazy ones below the fold
  never load).
- Button names come from `src/i18n.ts` (imported by Node 25.6 with types stripped), so copy edits do
  not break it; structural changes (the new tab bar, new screens) mean editing the state functions
  in `capture.mjs`. Each slice updates the harness with the screens it changes.
- Estimate data is real model output run through the server's own derivation
  (`Domain/Tools/Sugary/Estimation/MealEstimate.cs`: ×0.6–×1.6 widening unless high confidence,
  carbs per bound, summed total). English: benchmark meal "nvr 114" (lasagna, bread roll, steak) as
  google/gemini-3.8-flash answered it in the scorecard; the scorecard drops item notes and answer
  options, so those are written in the prompt's convention. Spanish: a real Spanish reply of the
  sugary agent from `.eval-output` (hamburguesa con patatas, with three questions and a note); its
  "after answers" version is written by hand from the answers picked.
  Photos are the benchmark's own.
- Contrast: `…/design/tools/contrast.mjs` recomputes every pair in §5.2; `tools/tnum.mjs` proves a
  font really switches to tabular figures.

## 1. Audit: what is wrong today

### 1.1 Flows in taps (before)

| Flow | Today |
|---|---|
| Photograph → estimate → answer 2 → save | Take a photo (1) → shutter (2) → Estimate carbs (3) → wait → 2 chips (4, 5) → Update estimate (6) → wait → scroll **≈800 px (EN) / ≈1,350 px (ES)** → Save to log (7). |
| Add a saved food | Meal: My foods card (1) → food (2) → Add to meal (3) → scroll ≈1 screen → Save (4). Breakfast of coffee + toast: 7 taps. |
| Scan a barcode | Packaged food (1) → Scan the barcode (2) → (reads) → Add to meal (3) → scroll → Save (4). |
| Read a label | Packaged food (1) → Photo of barcode or label (2) → shutter (3) → (reads) → Add (4) → scroll → Save (5). |
| Manual entry | Add by hand (1) → name, carbs → Add to meal (2) → scroll → Save (3). |
| Record an outcome later | Log (1) → meal (2) → scroll past a 220 px photo and the items → chip (3) → scroll → Save changes (4). Nothing in the Log says which meals are still waiting. |
| Find an old meal | Log → scroll; "Show older meals" every 50. No search, no filter. |
| Manage my foods | Settings (1) → scroll → Manage my foods (2) → food (3). |
| Download the log | Settings (1) → scroll → one of three equal buttons (2). |
| Sign out | Settings (1) → scroll to the bottom → Sign out (2), no confirmation. |

### 1.2 Problems, most serious first

Screenshot references are `before/<variant>/NN-state.png`.

**Things that can put a wrong number in front of her**

1. **Picked answers are silently not in the total.** Picking a chip fills it with the primary
   colour, so it looks applied, but the total does not move until "Update estimate" is tapped
   (`11-answers-picked`). Save stays available 1–2 screens further down and nothing warns. She can
   save, and dose from, a total that ignores her own answers.
2. **The best guess dominates; the range is small print.** "≈ 63 g" at 48 px; "Likely between 38
   and 100 g" at body size below it (`10-estimate`). The rations line mixes both ("6.5 rations of
   10 g · 4–10"). In Spanish the range wraps and the ration range breaks across lines ("6–" /
   "16,5") (`es-dark/10-estimate-full`).
3. **"Answering these tightens the estimate" is often untrue.** The server keeps at least
   ×0.6–×1.6 of the best weight for anything not marked high confidence, and the prompt tells the
   model to keep a picked option at medium. Lasagna is 21–56 g before and after its answer
   (`12-after-answers`). The copy must say what answers do: move the total.
4. **After a failed update, the old numbers stay on screen with no word that they are old.** The
   error sits at the top ("The estimate took too long…"), the correction she typed is still in the
   box, and the total above is the one from before her correction (`21-estimate-error`). There is
   no retry button (`tryAgain` exists in i18n and is unused).
5. **Confidence and "already counted" are a 10 px coloured dot plus 13 px grey text.** Rough guess
   uses the error red; a food from her list is a purple dot (`16-estimate-with-counted`). Colour is
   the only fast signal, and it collides with the error colour.

**Effort and navigation**

6. **Save is one to two screens down on every estimate**: its top sits ≈1,540 px down in English
   and ≈2,090 px in Spanish on an 844 px screen (`10-estimate-full`), so ≈800 / ≈1,350 px of
   scrolling past the questions, items, correction box and three add cards. The disclaimer lives
   next to Save, far from the number it qualifies. One-handed at a table, this is the most-repeated
   scroll in the app.
7. **Outcomes are four taps and two scrolls per meal**, and the Log gives no prompt. "Not noted"
   is pre-selected and drawn as a filled primary chip, so it reads as the action
   (`25-meal-sheet`). Went low and Went high are the same red: colour-only, and a hypo (the
   dangerous one) looks like a high.
8. **Her foods have two homes and neither is first-class.** Picking lives on the Meal screen;
   editing lives under Settings → Manage my foods. The foods she uses most (coffee 30 uses, toast
   22 in the fixture) cost three taps each, every breakfast.
9. **The empty Meal screen offers seven ways in at near-equal weight** (photo, gallery, film,
   describe, my foods, packaged, by hand) plus a three-line film hint; "Add by hand" is cut by the
   tab bar at first view; "Estimate carbs" appears only once there is input and shifts the layout
   (`03-meal-empty`).
10. **Two "Update estimate" buttons** can be on screen at once (questions card and correction form);
    the correction one sits as a full-width pale disabled button that looks broken (`10-estimate-full`).
11. **Finding an old meal is scrolling.** Log rows join item names with commas in the model's
    lower case ("spaghetti with meat sauce, shredded…"), show the best guess only, label day totals
    with a bare "99 g", and show English times as "02:25 PM" (`toLocaleTimeString("en")`) while she
    lives on a 24-hour clock; the edit sheet's date field is US format (`24-log`, `25-meal-sheet-bottom`).

**Detail, consistency, reach**

12. **Targets under 44 px** (`targets.txt`): photo/clip remove 28×28, every chip 40 px tall (answer
    options, outcomes, amount shortcuts), item Remove/Done 40 px; item meta text is 13 px and
    section labels 12.8 px uppercase grey.
13. `window.confirm` for discard, delete meal and delete food: the browser's dialog, not the app's.
14. A clip thumbnail looks exactly like a photo (no play mark, no duration), and the fourth
    thumbnail hides in a horizontal scroll with no affordance (`08-meal-with-clip`).
15. The packaged result does not say where its numbers came from (product database or a label
    read by a model) before she adds it (`19-packaged-found`).
16. Settings: three equal full-width download buttons, a long About paragraph, sign out with no
    confirmation (`27-settings-full`).
17. Brand chrome disagrees with itself: manifest `theme_color #5b34c9` vs `<meta theme-color>`
    `#f6f2ec`; the maskable icon is the same file as the "any" icon; the icon (ring with a diamond)
    means nothing (`01-sign-in`).

What works and stays: the in-app camera with a big shutter and a 20 s timer; the live scanner with
its guide, torch and "type the digits" fallback; sheets with a back arrow; the toast-then-Log after
saving; the edited/counted logic in `draft.ts`; the disclaimer wording; rations next to grams.

## 2. Information architecture

### 2.1 Navigation

A bottom tab bar with four tabs, labels always visible:

| Tab | EN / ES | Icon | What it is for | Its one primary action |
|---|---|---|---|---|
| Meal | Meal / Comida | plate with fork | The meal she is about to eat: capture, estimate, adjust, save. Stays mounted so a meal in progress survives a look elsewhere (as today). | Empty: **Take a photo**. With input: **Estimate carbs**. With items: **Save to log** (or **Update with your answers**, §3.3). |
| Log | Log / Diario | open book | What she ate and how it went; record outcomes; find an old meal. | **Record how it went** on the pending cards (inline buttons). |
| Foods | Foods / Alimentos | jar | Her pantry: everything she keeps, searchable, editable, one tap to add to the meal. | **New food**. |
| Settings | Settings / Ajustes | sliders | Rations, language, her data, about, sign out. | None (a settings list has no primary; that is fine). |

Why four, on the audit's evidence: Foods gets a tab because it is the fastest way to count an
at-home meal (problem 8) and its management is buried today. Settings stays a tab rather than a
header icon so the bar is consistent and nothing hides. A small count badge on Log shows meals
from the last 48 h still waiting for an outcome (`aria-label` "Log, 2 meals waiting for how it
went").

Sheets stay the pattern for anything that comes back to the screen it came from (amount for a
food, packaged food, by hand, a meal's details, a portion edit, confirmations). Every
`window.confirm` becomes a small confirmation sheet with the destructive action named on its
button.

### 2.2 Flows after (taps)

| Flow | Before | After |
|---|---|---|
| Photograph → estimate → answer 2 → save | 7 taps + 1–1.6 screens of scroll; answers can be saved unsent | 7 taps, **no scroll** (sticky footer); unsent answers turn the footer into "Update with your answers" |
| Add a saved food | 4 taps + scroll (7 for coffee + toast) | From **Your usuals** chips on the Meal screen: chip (1) → Add (2) → Save (3); coffee + toast: 5. From the tile: 4, no scroll. From the Foods tab: "+" (1) → Add (2), then Meal → Save. |
| Scan a barcode | 4 + scroll | The "Barcode or label" tile opens straight into the live scanner where it works: tile (1) → (reads) → Add (2) → Save (3). |
| Read a label | 5 + scroll | Same taps, no scroll, and a provenance line before Add. |
| Manual entry | 3 + scroll | 3, no scroll. |
| Record an outcome | 4 + 2 scrolls, no prompt | Log (1) → outcome on the pending card (2). Saved at once, with Undo in the toast. |
| Find an old meal | scroll, pages of 50 | Log → search field filters loaded meals by food name; "Search older meals" loads further pages until found. |
| Manage my foods | 3 + scroll | Foods (1) → food (2). |
| Download the log | 2 + scroll | Settings (1) → "Your data" row (2). Rare task; no change in depth. |
| Sign out | 2, no confirmation | Settings (1) → Sign out (2) → confirm (3). |

### 2.3 Screens

**Meal, empty.** Top: the Sugary wordmark (display face) with the slice mark, small. Title
"What's on the plate?". One big primary button, **Take a photo** (≥ 88 px tall, full width). Under
it a row of two equal secondary buttons: **Film it** (sub-label "up to 20 s") and **From gallery**.
The "describe it" field (optional, one line growing to three). **Your usuals**: up to six chips of
her most-used foods (by `uses`, then `lastUsedAt`), tap → amount sheet with her usual amount
already in → Add. **No photo? Add it another way**: three equal tiles in one row: My foods ·
Barcode or label · By hand (each ≥ 72 px, icon over label). The long filming hint moves to the
camera screen. The primary footer slot is empty until there is a photo, clip or description, then
**Estimate carbs** appears in the sticky footer (no content shift).

**Meal, with photos/clip.** A thumbnail strip (88 px squares, a visible fourth via wrap, not hidden
scroll); a clip shows a play mark and its length; each thumbnail has a remove button with a 44 px
hit area. Take another / Film / Gallery become compact secondaries.

**Estimating.** The first photo, "Looking at your plate…", "Usually 10–30 seconds.", and a real
**Cancel** button (secondary, 48 px). The tab bar stays.

**Estimate.** See §3.

**Log.** Title, then a search field. **How did it go?**: cards for meals with outcome "unknown"
eaten in the last 48 h (newest first, up to three, "Show all" beyond): thumbnail, foods, time,
carbs, and three 48 px buttons with icon + text: ↓ Went low · ✓ In range · ↑ Went high. Then the
days: header "Today · 99 g carbs" (labelled), rows with thumbnail, food names (each sentence-cased,
joined with " · ", two lines max), 24-hour time, an outcome badge with icon + text (↓ cherry, ✓
pistachio, ↑ caramel; "Not noted" muted), and on the right the logged carbs (tabular) with rations
under it. Tap a row → meal sheet: time and total with range at the top, **outcome as a four-way
segmented control** where "Not noted" is a neutral segment, then items, then the photo (160 px),
notes, when; **Save changes** pinned at the sheet's bottom; Delete meal as a quiet danger button →
confirmation sheet.

**Foods.** Title "Your pantry"; search; list sorted by use. Row: name, "8.9 g carbs per 100 ml ·
usually 200 ml", a small source icon (barcode, label, by hand); a trailing 44 px "+" adds it to the
meal (amount sheet → Add → toast "Added to your meal · View"). Row tap → edit. Primary **New food**
in the sticky footer. The meal's "My foods" tile opens the same list as a pick-only sheet (no edit
link; editing lives here).

**Settings.** Grouped list: One ration is (segmented 10 g / 15 g, with its hint); Language
(segmented); Your data (three rows: Meals · Foods in each meal · My foods, each with a file icon and
"CSV" — one hint line for the group); About the estimates (collapsed to two lines with "More");
Sign out (row, confirmation sheet).

**Sign-in.** The slice icon large, wordmark, tagline, "Access code" field with show/hide, **Come
in** right under the field (so the keyboard never covers it). Wrong code: plain error under the
field.

**Camera and scanner.** Keep the current structure (it works); restyle to tokens: close and flip
are 52 px with a visible white glyph on 45 % black; the shutter stays white and recording stays
red (camera conventions beat brand colour here); the status pill shows "Move slowly around the
plate. Sound is never sent." before filming and the timer while filming. Scanner: corner-marked
guide, status line, torch, "Type the digits instead" link, Stop.

## 3. The estimate screen

### 3.1 Hierarchy, top to bottom

1. **Header row**: thumbnails (48 px) of what was sent, and **Start over** as a text button on the
   right (→ confirmation sheet). No other chrome.
2. **The total, range first.** Label "Carbs in this meal". The range **"38–100 g"** is the largest
   thing on screen (44 px, bold, tabular, never wrapping: the range is one `nowrap` unit). Under
   it: "Best guess 63 g" and "6.5 rations of 10 g (4–10)". The range bar keeps its best-guess mark.
   Directly under the bar, small, the framing: "An estimate, not a measurement. Check it against
   what you know before dosing." When every item is confirmed, the range collapses to a single
   number with "From amounts you confirmed". If questions are waiting, a small link "Questions
   below (2)" jumps to them. Screen readers get one sentence, announced politely when it changes:
   "Between 38 and 100 grams of carbs, best guess 63."
3. **Items**, two groups:
   - **On your plate** (estimated): each row shows the name (sentence-cased for display), the
     portion, a confidence badge, and on the right the item's range "21–56 g" bold with "≈ 35" under
     it. The confidence badge is text plus a three-step meter glyph (1, 2 or 3 filled bars): Rough
     guess (caramel), Fair guess (milk chocolate), Confident (pistachio). Never the error red.
     "Corrected by you" replaces the confidence once she edits it.
   - **Already counted**: items from her foods, a barcode, a label or by hand, on the counted
     surface (pale pistachio cream), each with a check-circle icon, a source badge ("From your
     foods", "Barcode", "Label", "By hand") and one exact number, no range. A one-line explanation
     under the group title: "Exact amounts. The estimate leaves these out."
   - Model notes (the `notes[]`) sit under the items as info rows, not inside the total.
   - Tap any row → the **portion sheet**: item name, the model's note, the per-100 g figure, a large
     weight stepper (−10 / +10 g, 56 px), quick chips relative to the estimate (½ · ¾ · As estimated
     · 1½ · ×2), a carbs field for when she knows the carbs instead, **Done** (primary, bottom) and
     **Remove from meal** (quiet danger, separated, left).
4. **Questions**: a card titled "A few questions" with the hint "Your answers can change the
   total: they are what a photo shows worst." Each question is a legend and chips that wrap, every
   chip ≥ 44 px, full text, the selected one filled. Answering does not send anything by itself
   (each estimate is a paid model call).
5. **Something off?** collapsed to a single row; tapping it opens the text box with its own
   **Send** button. No permanently disabled big button.
6. **Add to this meal**: the same three tiles as the empty screen, compact.
7. **Sticky footer** (above the tab bar, safe-area aware): exactly one primary.

### 3.2 The footer's single primary

| State | Primary | Secondary |
|---|---|---|
| Items, nothing pending | **Save to log** | — |
| An answer picked and not sent | **Update with your answers** | "Save without them" (quiet) + a line above the buttons: "Your answers aren't in the total yet." |
| Correction typed and not sent | **Send your correction** (the box's own Send does the same) | "Save without it" (quiet) + the line "Your correction isn't in the total yet." |
| Updating | footer disabled, the estimating view replaces the content as today | Cancel |
| Update failed | **Try again** | "Save as it is" (quiet); the notice above the total says "The numbers below are from before your last change." |

### 3.3 What must not change

The draft logic (`draft.ts`, `estimate.ts`): the model's items are replaced on re-estimate and
counted items are kept; corrections and removals are sent as facts; `firstEstimate` is kept. The
redesign is presentation and flow; any logic it needs (unsent-answers state, usuals ordering,
pending outcomes) gets a unit test.

## 4. Visual direction: cake

The idea: a bakery counter. Cream and sponge surfaces, raspberry frosting for the one thing to
press, dark chocolate for text, a few sprinkles for delight away from the numbers. Dark mode is a
dark-chocolate counter, not an inverted pink.

### 4.1 Tokens (one place: `src/tokens.css`, imported first)

All colours, type, space, radius, shadow and motion live as custom properties on `:root`, with the
dark set in the existing `@media (prefers-color-scheme: dark)` block and `color-scheme: light dark`
kept. No hex outside `tokens.css` (camera/scanner chrome may use `#000`/`#fff` for the viewfinder
only).

| Token | Light | Dark | Role |
|---|---|---|---|
| `--color-bg` | `#FBF5EC` cream | `#1C1310` dark chocolate | page |
| `--color-surface` | `#FFFDF9` icing | `#281B16` | cards, sheets, inputs |
| `--color-sponge` | `#F3E6D3` sponge | `#33241D` | recessed: segmented track, chips at rest, tiles |
| `--color-counted` | `#EAF2EC` pistachio cream | `#1F2E24` | already-counted items |
| `--color-line` | `#E3D0B8` | `#4D382E` | dividers (decorative) |
| `--color-field` | `#A88B74` | `#8C7060` | input and chip borders (≥ 3:1) |
| `--color-ink` | `#3B2219` dark chocolate | `#F7EBDD` cream | text, numbers |
| `--color-muted` | `#6E4F40` milk chocolate | `#CDB3A0` | secondary text |
| `--color-primary` | `#B0234F` raspberry | `#F48FB1` raspberry frosting | primary buttons, selected chips, active tab |
| `--color-on-primary` | `#FFFFFF` | `#2A0D18` | text on primary |
| `--color-primary-soft` | `#FBE4EB` | `#46202F` | secondary buttons |
| `--color-primary-strong` | `#8E1A3F` | `#FFB8CE` | text on primary-soft, links |
| `--color-good` | `#2D6A43` pistachio | `#8FD3A6` | In range, Confident |
| `--color-caution` | `#7F5200` caramel | `#EDC074` | Went high, Rough guess |
| `--color-danger` | `#B3261E` cherry | `#FF8A80` | errors, Went low, destructive |
| `--color-danger-soft` | `#FCE8E6` | `#3D1F1B` | error notice background |
| `--color-info` / `--color-focus` | `#2F5DA8` blueberry | `#9DBCF2` | info icons, the focus ring |
| `--sprinkle-pistachio` | `#7DB38A` | same | decoration only |
| `--sprinkle-lemon` | `#E9B949` | same | decoration only |

Sprinkles never carry text or meaning; they appear in the icon, the empty-state drawings and the
sign-in.

### 4.2 Contrast (computed, WCAG 2.x; `tools/contrast.mjs`)

Text needs 4.5:1, borders/focus/large text 3:1. Every pair the UI uses:

| Pair | Light | Dark |
|---|---|---|
| ink on bg | 13.56 | 15.55 |
| ink on surface | 14.47 | 14.21 |
| ink on sponge | 11.95 | 12.67 |
| ink on counted | 12.89 | 12.13 |
| muted on bg | 6.78 | 9.18 |
| muted on surface | 7.24 | 8.38 |
| muted on sponge | 5.98 | 7.48 |
| muted on counted | 6.45 | 7.16 |
| on-primary on primary | 6.56 | 8.05 |
| primary on bg (text, active tab) | 6.06 | 8.18 |
| primary on surface | 6.46 | 7.48 |
| primary on sponge (selected marks, 3:1) | 5.34 | 6.67 |
| primary-strong on primary-soft | 7.34 | 8.62 |
| primary-strong on surface | 8.72 | 10.33 |
| good on surface | 6.35 | 9.56 |
| good on counted | 5.66 | 8.16 |
| caution on surface | 6.65 | 9.85 |
| caution on sponge | 5.49 | 8.78 |
| danger on surface | 6.43 | 7.31 |
| danger on danger-soft | 5.55 | 6.52 |
| info on surface | 6.36 | 8.67 |
| focus on bg (3:1) | 5.96 | 9.49 |
| focus on surface (3:1) | 6.36 | 8.67 |
| field border on surface (3:1) | 3.13 | 3.66 |
| bg on ink (toast) | 13.56 | 15.55 |

A new pair is added to `contrast.mjs` before it is used.

### 4.3 Type

- **Display: Fraunces** (`@fontsource-variable/fraunces/soft.css`, 62 KB for latin, which carries
  the `wght` axis as well as `SOFT`: its `@font-face` declares `font-weight: 100 900`; `SOFT` 100,
  weight 600):
  a soft, slightly gooey serif that reads as bakery lettering. Screen titles, sheet titles, the
  wordmark, empty-state headlines. **Never** numbers, buttons, labels, chips or body.
- **UI: Atkinson Hyperlegible Next** (`@fontsource-variable/atkinson-hyperlegible-next`),
  designed by the Braille Institute for legibility (distinct 0/O, 1/l/I), full Spanish set.
  Verified in Chromium: proportional by default, tabular with `tnum` (1111 and 8888 both 101 px at
  40 px). `font-variant-numeric: tabular-nums` on `body` so **every number** is tabular.
- Both self-hosted via `@fontsource`, latin + latin-ext only, `font-display: swap`, cached by the
  service worker (add `woff2` to the workbox `globPatterns`, which today stop at `wasm`).
- Scale (px, line-height): range hero 44/48 bold; screen title (Fraunces) 30/36; sheet title
  (Fraunces) 22/28; item/row name 17/24 bold; body 17/26; meta 15/22; field label 14/20 semibold;
  the minimum anywhere is 14. Section labels become sentence-case 15 px semibold muted (no
  letter-spaced uppercase).

### 4.4 Space, radius, shadow

- Space (4-based): 4, 8, 12, 16, 20, 24, 32, 40 (`--space-1`…`--space-10`). Page gutter 16; card
  padding 16; gaps in lists 12.
- Radius: `--radius-s` 10 (inputs), `--radius-m` 16 (buttons, tiles), `--radius-l` 22 (cards),
  `--radius-xl` 28 (sheet tops, the total card), pill 999 (chips, badges).
- Shadow, light: `--shadow-1` `0 1px 2px rgb(59 34 25 / 8%)` (cards);
  `--shadow-2` `0 2px 6px rgb(59 34 25 / 6%), 0 12px 28px rgb(59 34 25 / 10%)` (sheets, sticky
  footer). Dark: none; elevation by surface colour plus a 1 px `--color-line` edge.

### 4.5 Motion

`--dur-fast` 120 ms (press, chip select), `--dur` 200 ms (toast, total crossfade), `--dur-slow`
280 ms (sheet in, range bar); `--ease-out` `cubic-bezier(.2,.8,.2,1)`. Buttons press to
`scale(.98)`. When the total changes, the numbers crossfade and the range bar slides; nothing
counts up (a number that animates through values is a number she might read mid-way). Under
`prefers-reduced-motion: reduce`: no transforms, durations ~0, the estimating pulse and spinner
stop (text carries the state).

### 4.6 Iconography

Keep the current hand-drawn set (24 px grid, 1.8 stroke, round caps, `currentColor`) and extend
it in the same hand: slice (brand), plate-fork (Meal tab), book (Log), jar (Foods), search, play,
arrow-down, arrow-up, check-circle, meter-1/2/3, trash, file, torch (moved in from the scanner),
more. 24 px in buttons and tabs, 20 px inline, 18 px in badges. Every icon-only button has an
`aria-label`; every icon next to text is `aria-hidden`.

### 4.7 Decoration rules

One motif: a frosting drip, used only on the sign-in hero and the empty-state drawings (cake
stand for the empty log, jar for the empty pantry, slice for the empty meal). No confetti, no
sprinkles on numbers or over content, no decorative animation.

### 4.8 App icon, manifest, theme colour

- `public/icon.svg`: a cake slice in three-quarter view on a cream rounded square: two golden
  sponge layers (`#F2C879`), an icing-white filling, raspberry frosting (`#B0234F`) on top with two
  drips, three sprinkles (pistachio, lemon, icing). Flat shapes, no outlines, no text.
- `public/favicon.svg` (new): the slice silhouette alone in raspberry, readable at 16 px.
- `icon-192.png`, `icon-512.png` (purpose `any`): rendered from `icon.svg`.
- `icon-maskable-512.png` (new; today the maskable entry reuses `icon-512.png`): full-bleed cream,
  the slice inside the central 60 % so any mask keeps it whole.
- `apple-touch-icon.png` 180×180, opaque cream background (iOS adds no padding).
- PNGs rendered from the SVGs with Playwright in a scratch script; only SVGs and PNGs are committed.
- Manifest: `theme_color` and `background_color` `#FBF5EC` (cream, so the splash and the status
  bar match the light app); `description` "Carb counting from a photo"; icons as above.
- `index.html`: `<meta name="theme-color" content="#FBF5EC" media="(prefers-color-scheme: light)">`
  and `content="#1C1310"` for dark; `<link rel="icon" href="/favicon.svg">`.

## 5. Voice

The joke lives in tone: the name (Sugary, cake), the icon, the tagline, titles and empty states.
**Hard rule: never playful about numbers, ranges, confidence, errors, or anything she doses
from.** Ranges stay "32–48 g", confidence stays visible on every estimated item, "an estimate, not
a measurement" stays next to the total. Errors are plain and say what to do next. English and
Spanish are written separately; the Spanish is Spain's Spanish (hidratos, raciones, móvil), `tú`,
and uses its own idioms rather than translating English puns.

### 5.1 English

| Key | Copy |
|---|---|
| tagline (new) | Carb counting, a piece of cake. |
| signInPrompt | Access code |
| signIn | Come in |
| tabFoods (new) | Foods |
| captureTitle | What's on the plate? |
| captureHint | Whole plate in frame, with a fork or a glass beside it for scale. |
| filmPlate | Film it |
| filmPlateSub (new) | Up to 20 s |
| usualsTitle (new) | Your usuals |
| otherWaysTitle (new) | No photo? Add it another way |
| packaged (tile) | Barcode or label |
| manual (tile) | By hand |
| estimating | Looking at your plate… |
| estimatingHint | Usually 10 to 30 seconds. |
| totalLabel (new) | Carbs in this meal |
| totalBest (new) | Best guess {best} g |
| rationsLine (new) | {n} rations of {size} g ({low}–{high}) |
| totalSpoken (new) | Between {low} and {high} grams of carbs, best guess {best}. |
| exactTotal | From amounts you confirmed |
| questionsJump (new) | Questions below ({n}) |
| questionsTitle | A few questions |
| questionsHint | Your answers can change the total: they are what a photo shows worst. |
| updateWithAnswers (new) | Update with your answers |
| answersPending (new) | Your answers aren't in the total yet. |
| saveWithoutAnswers (new) | Save without them |
| itemsTitle | On your plate |
| countedTitle (new) | Already counted |
| countedHint (new) | Exact amounts. The estimate leaves these out. |
| confidence_low / _medium / _high | Rough guess / Fair guess / Confident (unchanged) |
| correctionToggle (new) | Something off? Tell it |
| correctionSend (new) | Send |
| sendCorrection (new) | Send your correction |
| correctionPending (new) | Your correction isn't in the total yet. |
| saveWithoutCorrection (new) | Save without it |
| addMore | Add to this meal |
| staleAfterError (new) | The numbers below are from before your last change. |
| saveAsIs (new) | Save as it is |
| discardTitle (new) | Discard this meal? |
| discardBody (new) | The photos, answers and foods you added go too. |
| discardYes / discardNo (new) | Discard / Keep it |
| logPendingTitle (new) | How did it go? |
| logPendingHint (new) | Tap what your glucose did after these meals. |
| outcomeNoted (new) | Noted: {outcome} |
| undo (new) | Undo |
| logSearch (new) | Search your log |
| logSearchNone (new) | No meal with that name in what's loaded. |
| logSearchOlder (new) | Search older meals |
| dayTotal | {n} g carbs |
| logEmpty | The display case is empty. |
| logEmptyHint | Save a meal and it shows up here. A couple of hours later, note how your glucose went. |
| logBadgeSpoken (new) | {n} meals waiting for how it went |
| foodsTitle (new) | Your pantry |
| foodsEmpty | The pantry is bare. |
| foodsEmptyHint | Scan a barcode or a label and tick "Save to my foods", or add one here. |
| addedToMeal (new) | Added to your meal |
| viewMeal (new) | View |
| sourceProduct (new) | From the product database (barcode {code}). |
| sourceLabel (new) | Read from your photo of the label. Check it against the pack. |
| signOutConfirm (new) | Sign out of Sugary? Your log stays saved. |

### 5.2 Spanish (written, not translated)

`pan comido` is Spain's "piece of cake", and bread is the carb everyone counts: the tagline is the
pun. "Lo de siempre" is what you say at your bar. "La vitrina" is the pastry shop's display case.

| Key | Copy |
|---|---|
| tagline | Contar hidratos, pan comido. |
| signInPrompt | Código de acceso |
| signIn | Entrar |
| tabFoods | Alimentos |
| captureTitle | ¿Qué hay en el plato? |
| captureHint | El plato entero, con un tenedor o un vaso al lado para tener escala. |
| filmPlate | Grabarlo |
| filmPlateSub | Hasta 20 s |
| usualsTitle | Lo de siempre |
| otherWaysTitle | ¿Sin foto? Añádelo de otra forma |
| packaged (tile) | Código o etiqueta |
| manual (tile) | A mano |
| estimating | Mirando tu plato… |
| estimatingHint | Suele tardar entre 10 y 30 segundos. |
| totalLabel | Hidratos de esta comida |
| totalBest | Lo más probable: {best} g |
| rationsLine | {n} raciones de {size} g ({low}–{high}) |
| totalSpoken | Entre {low} y {high} gramos de hidratos; lo más probable, {best}. |
| exactTotal | Con cantidades que has confirmado |
| questionsJump | Preguntas abajo ({n}) |
| questionsTitle | Unas preguntas |
| questionsHint | Tus respuestas pueden cambiar el total: es lo que peor se ve en una foto. |
| updateWithAnswers | Actualizar con tus respuestas |
| answersPending | Tus respuestas aún no están en el total. |
| saveWithoutAnswers | Guardar sin ellas |
| itemsTitle | En tu plato |
| countedTitle | Ya contado |
| countedHint | Cantidades exactas. La estimación no las incluye. |
| confidence_low / _medium / _high | Muy aproximado / Aproximado / Fiable (today's "Aproximación gruesa / razonable" wrap to two lines in a row) |
| correctionToggle | ¿Algo no cuadra? Cuéntalo |
| correctionSend | Enviar |
| sendCorrection | Enviar tu corrección |
| correctionPending | Tu corrección aún no está en el total. |
| saveWithoutCorrection | Guardar sin ella |
| addMore | Añadir a esta comida |
| staleAfterError | Los números de abajo son de antes de tu último cambio. |
| saveAsIs | Guardar tal cual |
| discardTitle | ¿Descartar esta comida? |
| discardBody | También se borran las fotos, las respuestas y lo que has añadido. |
| discardYes / discardNo | Descartar / Seguir con ella |
| logPendingTitle | ¿Cómo fue? |
| logPendingHint | Toca qué hizo tu glucosa después de estas comidas. |
| outcomeNoted | Apuntado: {outcome} |
| undo | Deshacer |
| logSearch | Buscar en el diario |
| logSearchNone | Ninguna comida con ese nombre en lo cargado. |
| logSearchOlder | Buscar en comidas anteriores |
| dayTotal | {n} g de hidratos |
| logEmpty | La vitrina está vacía. |
| logEmptyHint | Guarda una comida y aparecerá aquí. Un par de horas después, apunta cómo fue tu glucosa. |
| logBadgeSpoken | {n} comidas pendientes de cómo fue |
| foodsTitle | Tu despensa |
| foodsEmpty | La despensa está vacía. |
| foodsEmptyHint | Escanea un código de barras o una etiqueta y marca «Guardar en mis alimentos», o añade uno aquí. |
| addedToMeal | Añadido a tu comida |
| viewMeal | Ver |
| sourceProduct | De la base de datos de productos (código {code}). |
| sourceLabel | Leído de tu foto de la etiqueta. Compruébalo con el envase. |
| signOutConfirm | ¿Cerrar sesión en Sugary? Tu diario se queda guardado. |

Everything not listed keeps today's wording in both languages (errors, camera, scanner, food
form, disclaimer, outcomes). Plurals: `{n}` strings that can be 1 ("1 rations") get `_one`/`_other`
keys (`Intl.PluralRules`), in both languages.

## 6. Accessibility and ergonomics

- **Targets**: every control ≥ 44×44 px (chips 44, buttons 48, big primary 56, Take a photo ≥ 88,
  tab 64). Small visuals (a remove ×) get a 44 px hit area. `targets.txt` from the harness must be
  empty (the checkbox inside its 48 px label is the one allowed line).
- **Thumb reach**: every screen's primary sits in the bottom third: the sticky footer above the
  tab bar on Meal and Foods, the bottom of every sheet. Sheet close stays top-right but every sheet
  also closes by backdrop tap and Escape/back.
- **Safe areas** (`viewport-fit=cover`): screens pad `env(safe-area-inset-top)`; the tab bar,
  sticky footer, sheets, toast and camera bar pad `env(safe-area-inset-bottom)`; nothing fixed sits
  under the home indicator.
- **Focus**: a 3 px `--color-focus` ring with 2 px offset on every interactive element including
  chips, segments and tiles; never removed; visible on primary (offset ring over the cream gap).
- **Screen readers**: the total is announced in one sentence (`totalSpoken`, `aria-live="polite"`),
  the range bar is `aria-hidden`; ranges in rows have a spoken form ("21 to 56 grams"); chips and
  segments use `aria-pressed`; outcome buttons say what they record; the tab badge has its own
  label; dialogs are labelled and return focus.
- **Not by colour alone**: confidence = text + meter glyph; outcome = text + arrow/check; counted =
  group + surface + icon + badge text.
- **Both languages fit**: Spanish is the long one. No truncation of any number or range (ranges are
  `nowrap` units); names may clamp to two lines in the Log only; buttons wrap rather than clip.
  Check every slice in `es-*`.
- **Numbers**: tabular everywhere; 24-hour times in both languages (`en-GB` / `es-ES` formatting
  for dates and times; decimals follow the UI language: 6.5 / 6,5).
- **Dark mode**: the full token set; photos keep their own colours; no pure black or white
  surfaces outside the viewfinder.
- **Text size**: layouts hold at 130 % root font size (Android "large" text); verified with a
  harness run that sets it.

## 7. Build plan

Each slice is one or more commits on `sugary`, repo style (`feat(sugary): …`), never pushed. Before
each commit: `npm run typecheck`, `npm test` (vitest), and the harness in all four variants, with
the changed screens read side by side with `before/`. Use Node 25.6 (`~/.nvm/versions/node/v25.6.0`);
22.6 hangs `npm install`. Installing the two `@fontsource-variable` packages is the only new
dependency.

1. **Tokens, fonts, icon, manifest, shell, navigation.** `tokens.css`; fonts; base type and the
   button/chip/input/segment/notice/sheet primitives on tokens; the extended icon set; the app
   icon set and manifest/meta; the four-tab bar (Foods tab hosting today's foods list as a screen,
   Settings losing "Manage my foods"); confirmation sheet component replacing `window.confirm`;
   24-hour times; `woff2` in the workbox globs. Add `i18n.test.ts` (§8). *Verify*: no hex outside
   `tokens.css` (grep); `contrast.mjs` all pass; `targets.txt` shrinks to the chips that slice 2–4
   restyle; icons at 16/180/192/512 and the maskable one under a circle mask; manifest in the build
   output; dark screenshots read as chocolate, not purple.
2. **Meal and estimate.** Empty screen (usuals, tiles, film hint moved), thumbnails with clip
   badge, sticky footer and its state table, range-first total, grouped items with badges, portion
   sheet, questions card, correction disclosure, stale-after-error notice with Try again.
   *Verify*: the photo → answer → save flow has no scroll at 390×844 in both languages; picking an
   answer turns the footer to "Update with your answers"; unit tests for the footer state and the
   usuals ordering; `draft.test.ts`, `estimate.test.ts`, `carbs.test.ts` unchanged and green.
3. **Log and outcome.** Search, pending cards with inline outcome + Undo, tab badge, labelled day
   totals, outcome badges, the meal sheet reordered with a segmented outcome and pinned Save.
   *Verify*: outcome in two taps from launch; Undo restores "unknown" (PUT twice in the mock log);
   empty, one-day and many-day states; search finds a meal on page 2.
4. **Foods and the sheets.** Foods screen (search, "+", source icons, New food), pick-only foods
   sheet, amount sheet, packaged (tile opens the live scanner directly where it works; provenance
   line), manual. *Verify*: coffee + toast breakfast in five taps; a barcode flow with no scroll.
5. **Sign-in and settings.** *Verify*: wrong code, show/hide, keyboard does not cover Come in at
   390×844 with the keyboard height simulated (viewport 390×500); sign-out confirmation.
6. **Camera and scanner chrome.** *Verify*: with the fake camera, the photo, film-ready, filming and
   scanner states in both schemes; controls ≥ 52 px; status pill legible on a bright plate.

Slice 1 first pins the screenshot numbers in `capture.mjs` (today they follow call order, so one
step that fails shifts every later file name). Every slice extends `capture.mjs` for the states it adds (pending cards, portion sheet, search,
Foods tab, confirmation sheets, 130 % text) and keeps the old state names where the screen still
exists, so `before/` and the new folders compare one to one.

## 8. EN/ES key parity

No test enforces it. Parity is a compile-time check: `const es: Strings = { … }` with
`Strings = typeof en` makes a missing Spanish key a type error and an extra one an excess-property
error, and `tsc --noEmit` runs in `npm run build` and `npm run typecheck` (the server's Dockerfile
runs `npm run build`, so an image with a missing key does not build). Vitest does not check it,
and nothing checks that the `{placeholders}` match between languages, that no Spanish value is
left empty, or that none is a copy of the English. Slice 1 adds `src/i18n.test.ts` for those three.

## 9. Decisions taken here, open to her veto

- Times in 24-hour format in English too (she lives in Spain; today English shows "02:25 PM").
- Outcomes recorded with one tap from the Log, saved at once, with Undo, instead of a form with Save.
- Answers are sent only on "Update with your answers" (each update is a paid model call), but the
  footer will not let unsent answers go unnoticed.
- Spanish confidence labels shortened to Muy aproximado / Aproximado / Fiable.
