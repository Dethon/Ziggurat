# Architecture review — Ziggurat

**Date:** 2026-08-29 · **Commit:** `7ea4c7f7` · **Scope:** churn-led (web browsing, eval harness, agent/channel core)

Produced by three parallel explorers, then re-verified line by line against the working tree. Every count below is the verified one, not the reported one. No files were modified.

An HTML version with before/after diagrams sits beside this file at `report.html`.

## How to use this document

Each candidate is self-contained and carries its own file anchors, evidence and ADR position. To grill one, point a session at this file and name the candidate heading — e.g. *"read `.scratch/architecture-review/report.md`, then grill candidate 1"*. The `Open questions` section under each candidate is where a grilling should start.

**Vocabulary** (from the `/codebase-design` skill — use these exactly, don't drift to component/service/API/boundary):

- **Module** — anything with an interface and an implementation.
- **Interface** — everything a caller must know: signature, invariants, ordering constraints, error modes, config, performance.
- **Depth** — leverage at the interface. Deep = lots of behaviour behind a small interface. Shallow = interface nearly as complex as the implementation.
- **Seam** — a place you can alter behaviour without editing in that place.
- **Adapter** — a concrete thing satisfying an interface at a seam.
- **Leverage** (what callers get) · **Locality** (what maintainers get).
- **Deletion test** — delete the module: does complexity vanish (pass-through) or reappear across N callers (earning its keep)?

---

## Summary

| # | Candidate | Strength | Area |
|---|---|---|---|
| [1](#candidate-1--give-the-tab-protocol-a-module) | Give the tab protocol a module | **Strong** | Web browsing |
| [2](#candidate-2--one-label-ladder-two-renderers) | One label ladder, two renderers | **Strong** | Web browsing |
| [3](#candidate-3--move-the-guard-onto-the-scenario) | Move the guard onto the scenario | **Strong** | Eval harness |
| [4](#candidate-4--make-the-conversation-identity-a-type) | Make the conversation identity a type | **Strong** | SignalR channel |
| [5](#candidate-5--give-the-body-window-an-owner) | Give the body window an owner | Worth exploring | Web browsing |
| [—](#deferred--scenario-is-wide-but-hold-off) | Tolerance profile for `Scenario` | Deferred | Eval harness |

**Top recommendation: [candidate 1](#candidate-1--give-the-tab-protocol-a-module).**

Two findings are defects rather than shape complaints:

- **Live silent chunk drop** — [candidate 4](#candidate-4--make-the-conversation-identity-a-type). A reply can vanish with only a log warning.
- **Evaporating coverage** — [candidate 2](#candidate-2--one-label-ladder-two-renderers). Three of four byte-acquisition rungs are covered only by tests that navigate to live JPL/Wikipedia/Reddit behind `Skip.If`.

---

## Why these areas

Scoped by churn before scanning, per YAGNI.

| Signal | Value |
|---|---|
| Fixes vs features, last 200 commits | 84 vs 56 |
| Dominant scope tags | `web` (44), `eval` (47) |
| Test churn vs production churn | 3.4× |
| Largest production file | `PlaywrightWebBrowser.cs` — 1780 lines, 2.3× the next |
| Test reaches past the browse seam | 34, across 8 files |

Grouping the ~30 consecutive `fix(web):` commits by actual cause:

- **~11** — a call site forgot part of the tab protocol (sequencing) → [candidate 1](#candidate-1--give-the-tab-protocol-a-module)
- **~7** — two copies of one definition drifted (the label ladder) → [candidate 2](#candidate-2--one-label-ladder-two-renderers)
- **~6** — one coordinate system disagreed with itself (paging) → [candidate 5](#candidate-5--give-the-body-window-an-owner)

None were policy bugs. The pure functions were extracted for testability and the real bugs live in how they are called — locality is the missing property, not correctness.

A corroborating signal: `.claude/rules/web-browsing.md` contains three separate invariants of the form *"written twice and must move as one"*, *"spelled three times"*, *"compute that number independently on either side"*. Those are invariants held by prose because no module holds them.

---

## Candidate 1 — Give the tab protocol a module

**Strength: Strong** · in-process · Web browsing

### Files

| Path | Lines | Anchors |
|---|---|---|
| `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` | 1780 | 266–288, 172/230, 370–393, 450–508, 541, 629–648, 787–845, 854–862, 931–949 |
| `Infrastructure/Clients/Browser/BrowserSessionManager.cs` | 712 | whole |
| `Tests/Unit/Infrastructure/BrowserSessionManagerTests.cs` | 682 | 36 facts |
| `Tests/Integration/Clients/BrowseTabsTests.cs` | 231 | reaches `browser.Sessions` |

### Problem

`BrowserSessionManager` is deep on the inside and shallow at its edge. It exposes **47 public members** — nine of them the tab protocol proper (`AcquireTabForBrowseAsync`, `LockTabAsync`, `TouchTab`, `NoteFinalUrl`, `MarkTabNavigated`, `BeginStampAsync` + `RefStampLease.Commit`, `RouteRef`, `TakePendingPopup`, `AdoptPopupAsync`). `PlaywrightWebBrowser` calls into that surface at **33 sites** (verified), and each of the five operations re-derives the same ordering by hand:

```
route or acquire → lock → re-check Page.IsClosed *under* the lock and answer the closed wall
→ touch → read page.Url under the lock → do the work → MarkTabNavigated if the URL moved
→ NoteFinalUrl → stamp under a lease with the right supersede flag
→ catch IsConnectionClosed and disambiguate "this tab died" from "the connection died"
```

That ordering **is** the interface, in the deep-module sense, and it exists only in prose — comments at 92–94, 182, 263–265, 456, 472–473, 789–791, 929–930, plus "Never acquire the pool gate while holding a tab lock" in the rules file.

Evidence that it is not learnable from the interface — each step was added by its own bug fix:

- The under-lock `IsClosed` re-check appears **three times** (382, 474, 936); `18eca2fc` and `2fcd76fd` each found one missing.
- The closed-vs-connection-dead disambiguation appears **three times in two spellings** (458–462, 497–501, and differently at 946–948 as `Status == SiteRefused && Page.IsClosed`).
- `MarkTabNavigated`-before-restamp appears **three times with three different conditionals** (172 always, 640 `if navigationOccurred`, 837 `if page.Url != urlBefore`).

**Why the tests never caught these.** `BrowserSessionManagerTests` drives the verbs directly against `Mock<IPage>` — so it tests the *policy* (LRU, ranges, tombstones, counters) perfectly and can never observe an ordering mistake, **because it is the caller**. It performs the sequencing rather than checking it. 36 facts, green throughout all eleven sequencing bugs.

The codebase says this out loud. `PlaywrightWebBrowser.cs:1223-1225`:

```csharp
// The pool as the policy layer sees it, for integration tests that assert on which tabs are
// alive rather than inferring it from tool results. Nothing in production reads this.
internal BrowserSessionManager Sessions => _sessions;
```

A test-only accessor is the standard tell that the real interface cannot express what needs asserting. Verified: **34 reaches** past the seam (`browser.Sessions`, `EvaluateOnSessionAsync`, `AnnotateImagesOnSessionAsync`, `EnsureInitializedAsync`) across 8 test files.

**Deletion test.** Delete `BrowserSessionManager` today and the LRU/registry/counter policy reappears in one place — it earns its keep. Delete it *as the interface it currently presents* and nothing is saved, because the caller still has to know every ordering rule.

### Solution

Move the seam from *"nine verbs the caller sequences"* to *"one entry point that takes what the operation **is** — a browse of this URL, a call routed by this ref, a ref-less call on the current tab — plus the work to do on the page, and hands back either a routed page or the named wall."*

The manager then owns, once each: acquisition-or-routing, the lock, the under-lock closed re-check, the touch, the before-URL read, navigation detection, `NoteFinalUrl`, the stamp lease with its supersede decision, popup adoption, and the closed-vs-disconnected disambiguation. `PlaywrightWebBrowser` shrinks to Playwright choreography — goto, wait, dismiss, extract, act, screenshot — with no session bookkeeping in it.

The tab-protocol constants currently sitting in `PlaywrightWebBrowser` (the three-attempt bound at 273, `RefResolutionTimeoutMs`) move with the policy they belong to.

### Structure

**Before:** `McpWeb*Tool` → `Web*Tool` → `IWebBrowser` → `PlaywrightWebBrowser`. From there, five thick arrows into `BrowserSessionManager` (Navigate, Snapshot, Action, Back, FetchImages), each really 5–8 thin arrows to separate verbs. A sixth arrow runs *backwards* — `Page.Popup` → `AdoptPopupAsync` → `PendingPopup` — which the browser must `TakePendingPopup` at two different points (541, 629).

**After:** one arrow per operation, `RunOnTab(intent, work)`, fanning to private verbs inside. The popup back-arrow is absorbed into the action intent. `PlaywrightWebBrowser` keeps only its side arrows to `AccessibilitySnapshotService`, `ModalDismisser`, `HtmlProcessor`, `ImageFetchPayload`.

**Mass:** `BrowserSessionManager` grows slightly in mass, shrinks a lot in perimeter (47 members → one entry point). `PlaywrightWebBrowser` loses ~250 lines and, more importantly, the perimeter it had against the manager.

### Benefits

- **Locality** — "what happens to a tab when a call touches it" becomes one file read top to bottom, not a rule reconstructed from eight scattered comments across 1780 lines.
- **Leverage** — the five operations stop repeating a nine-step preamble. Three `IsClosed` spellings become one; three `MarkTabNavigated` conditionals become one.
- **Tests** — the interface becomes the test surface. The 36 policy facts can then assert *ordering* against a faked page ("an eviction landing between routing and locking answers the closed wall"; "a non-navigating action leaves earlier ranges routing") — today reachable only through live Camoufox. A whole class of the eleven sequencing fixes becomes a unit test failing in milliseconds. `Sessions` and at least two `EnsureInitializedAsync` reaches disappear.

### ADR position

**No conflict — ADR-0034 already asks for this.** It states the pool policy lives in `BrowserSessionManager` and "`PlaywrightWebBrowser` only carries pages around." Today that sentence is aspirational; this makes it true. No decided behaviour changes: three tabs, LRU, session-unique refs, the two walls, the composed-snapshot exception all stand.

### Open questions for grilling

1. What is the right shape for "intent"? A closed set of five (browse / routed / current-tab / popup-adopt / fetch), or an open one?
2. Does `work` take the page, or a narrower handle that cannot outlive the lock? The latter is safer but may fight Playwright's API.
3. Popup adoption currently spans the seam. Does it fold cleanly into the action intent, or does it need its own?
4. The composed-snapshot exception (`SnapshotRequest.ForUrl`, refusing if the tab is gone) is a special case in ADR-0034. Where does it live after the move?
5. Is 47 members → 1 too aggressive — do any callers legitimately need a verb directly?

---

## Candidate 2 — One label ladder, two renderers

**Strength: Strong** · ports & adapters · Web browsing

### Files

| Path | Lines | Anchors |
|---|---|---|
| `Infrastructure/HtmlProcessing/PageImageEntry.cs` | 145 | `FirstSpoken` 97–102, `FileName` 109–120, `Sanitize` 128–132, `MaxLabelLength` 33 |
| `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` | 1780 | JS ladder **961–987**, inside `FetchOneAsync` 951–1102; `FetchAsServedAsync` 1108–1142; `ScreenshotAsync` 1152–1164 |
| `Infrastructure/Clients/Browser/ImageFetchPayload.cs` | 91 | `Parse` |
| `Tests/Unit/Infrastructure/PageImageEntryTests.cs` | 212 | covers C# side only |
| `Tests/Integration/Clients/{NoAcaoCdn,CrossOriginImageProbe,SameOriginSvg}FetchTests.cs` | 50/51/60 | live third-party sites |

### Problem

**The ladder is implemented twice, in two languages.** Verified side by side — six rungs (`alt` → `figcaption` → `title` → enclosing link text → filename → `WxH`), the same data-URI exclusion, the same `[`→`(` bracket rounding, the same whitespace flattening, the same 500-character cut with trailing ellipsis. `MaxLabelLength = 500` is a `private const` in C# and a **bare literal `500` twice** in the JS.

What holds them together is a comment — "Rung for rung, regex for regex… change both sides in one commit" — plus a paragraph in the rules file. That is not a seam, it is a promise, and it has failed twice on record:

- `b43dab5e` — the fetch sliced bare where the entry ellipsised.
- The `||`-vs-blank short-circuit: a whitespace-only `alt` collapsed every later rung to an empty label.
- `d33eab05` moved the cap and had to touch both files.

**The mechanism to fix this already works in that same block.** The JS interpolates `{{PageImageEntry.WidthAttribute}}` from C# today (line 979). The attribute names were taken along; the ladder was not.

**A second ladder in the same literal.** The four-rung byte-acquisition sequence (same-origin credentialed fetch → anonymous fetch → anonymous-`crossOrigin` canvas → element fallback → `{tainted}` → C#-side `FetchAsServedAsync` → `ScreenshotAsync`) is split at the wrong place: three rungs live in the JS string, two in C#, joined by a **stringly-typed protocol**. The JS returns `null`, `"never-loaded"`, `"error"`, `{tainted:true,label,url}`, or `{mediaType,label,data}`, decoded by `ImageFetchPayload.Parse`.

**Coverage that evaporates.** Three of the four acquisition rungs are, in practice, untestable in CI. Verified: `NoAcaoCdnFetchTests` navigates to `https://www.jpl.nasa.gov/images/` and `Skip.If`s when JPL is unreachable *or* "No jpeg in the gallery today". `CrossOriginImageProbeTests` and `SameOriginSvgFetchTests` are the same shape. Test hosts found across the integration suite include `jpl.nasa.gov`, `en.wikipedia.org`, `upload.wikimedia.org`, `reddit.com`.

**Deletion test on the JS ladder:** delete it and complexity does not vanish — it reappears in the C# ladder that already exists. That is a pass-through duplicate.

### Solution

Make the ladder a module rather than a convention. One definition — the rung list, the sanitiser rule, the cap — with **two renderings**: executed directly for the extraction half, emitted as JS for the in-page half. The seam moves from "two files a human keeps in sync" to "one definition with two renderers".

Separately, give the byte-acquisition ladder an interface: a module owning the rung sequence, answering a typed outcome, with each rung an adapter behind it. Pull the whole sequence to C# and let the page script answer only *facts* (resolved address, did the wire fetch succeed, did the canvas taint), so the ordering is testable without a browser and only the rung that genuinely needs a page needs one.

**Four adapters** (in-page wire fetch, in-page canvas, context request client, element screenshot) — a real seam, not a hypothetical one.

### Benefits

- **Locality** — "what does a picture get called" becomes one place; the two-sided edit hazard stops existing.
- **Leverage** — the ~130-line JS literal in `FetchOneAsync` drops to a fetch-facts probe. `PlaywrightWebBrowser` loses its largest block of non-C#.
- **Tests** — the rung ladder becomes unit-testable on both sides from one fixture set; "a long label is cut the way the entry cut it" stops needing Camoufox. Acquisition *ordering* becomes assertable against fakes, and the live-site tests shrink to "does the real CDN still behave this way" — which is what an external-category test should be, rather than the only coverage the ladder has.

### ADR position

**No conflict.** ADR-0033 decides the rung *order* and the credentials rule; both survive verbatim. This changes only how many times each is written.

⚠️ **Load-bearing constraint** — the cross-origin fetch must stay **anonymous** (`credentials: 'omit'`). The big image CDNs send ACAO `*`, and a wildcard is exactly what a *credentialed* request is rejected against. This broke the first implementation. Do not "fix" it to `credentials: 'include'`.

### Related, fold in rather than pursue separately

`HtmlConverter.ImageNumberer` (202–207) is a converter-local fallback counter used when the page's `data-img-ref` is absent — i.e. in hand-written test HTML. That means **every unit test of the image catalogue exercises a numbering path production never takes**, while the production path (`StripDomNoiseAsync`'s survivor-order counter at 1414–1424) is only covered by browser-dependent tests. `ac8cc93c` (a nested table repeating a ref) is precisely a disagreement between those two counters.

### Open questions for grilling

1. Emit the JS from C# at build time or runtime? Runtime is simpler; build time is debuggable.
2. Can the sanitiser rules (regex semantics, `trimEnd`, ellipsis) survive translation to JS faithfully, or do subtle .NET/JS regex differences bite?
3. Should the byte ladder's rungs be true adapters, or is that over-abstracting four fixed steps that never vary at runtime?
4. What replaces the live-site tests as *proof* the CDN behaviours still hold — a recorded fixture, or an external-category test run on a schedule?
5. Does folding `ImageNumberer` in mean unit tests must now supply `data-img-ref`, and does that make them less readable?

---

## Candidate 3 — Move the guard onto the scenario

**Strength: Strong** · in-process · Eval harness

### Files

| Path | Lines | Anchors |
|---|---|---|
| `Tests/Eval/ClaimExemptions.cs` | 208 | enum 9–38, `Exemption` 40, dictionary literal 49–208 |
| `Tests/Eval/ClaimCoverageTests.cs` | 92 | 31, 79–83, 87–91 |
| `Tests/Eval/ScorecardLedger.cs` | 122 | `Coverage()` 92–114, `Spelled` 111–120 |
| `Tests/Eval/Scenarios/*.cs` | 2180 | 16 "see ClaimExemptions" comments |

### Problem

The module's whole interface is three members — `Reasons`, `Exemption(Kind, Reason)`, `ExemptionKind` — and its whole implementation is a literal table. It fails the *locality* half of the deletion test badly: the thing it is a table of is **not a property of a claim**; it is a property of the **relationship between a scenario and a claim**, stored a file away from both.

Three pieces of evidence:

**1. The taxonomy has one live value.** Verified: **29 of 29 entries are `ExemptionKind.Guard`**. `Unwritten`, `NeedsFixture`, `Unfalsifiable`, `Judge` and `Finding` are declared, documented at 15–37, spelled into the scorecard at `ScorecardLedger.cs:116-120` — and used by nothing. Five of six branches of a six-way discrimination are dead. Every non-`Guard` entry was closed by backlog commits (`e39d1d97` −61 lines, `47f1a6b0` −42, `490691e6` −18). One kind is not a taxonomy.

**2. The churn is not backlog burn-down.** 25 touches in 150 commits, **+658 / −449** — nearly five rewrites. Recent ones are scenario-driven:

- `ec64d522` "a worker that declines is a detour the web scenarios survive" — **+4/−1**. A *scenario* changed its `WorkerAnswer`, and exemption prose had to be edited to match.
- `fa25a649` "the flakes the suite survives are kept" — **+49/−4**. Armed-run observations written into exemption prose.
- `3ee0a868`, `a1852842` — two delegation findings closing "by withdrawing their claims", +14/−51.

**3. The same fact is stated twice, and neither statement compiles against the other.**

`ClaimExemptions.cs:181-187`:
> "Demonstrated twice and stayed green both times, most recently on 2026-08-18 with all three mounts hosted… The scenario is kept as a regression guard against a model that stops discriminating; it just cannot earn the citation."

`MechanismScenarios.cs:48-51`, on the scenario that guards it:
> "No deterministic citation: both rules were deleted from every prompt that teaches them and this still passed. It runs as a regression guard rather than as evidence — see ClaimExemptions."

One is a compiler-checked key; the other is a comment ending in a hyperlink a human has to walk. Verified: **16** such cross-references across the scenario files.

The interface is nearly as complex as the implementation. A caller must know the dictionary is keyed by `PromptClaim.Id` (not the claim object), that `Reasons` must never intersect `Cited()` — `ClaimCoverageTests.cs:87-91` exists *solely* to police that — and that removing a line here "is what adding a scenario looks like" (comment at line 46). That last invariant is a comment because it cannot be a type.

### Solution

Move the guard relationship onto the scenario, where the compiler already lives. `Scenario` gains a second list beside `Claims`: the claim id it guards, plus the demonstration note. The claim id is already a compile-time symbol (`TimerPrompt.DurationIsACountdown.Id`), so a renamed or withdrawn claim breaks the scenario that guards it, in the file that guards it.

`ClaimExemptions` then shrinks to what it was designed for and no longer is: claims **no scenario touches at all**. On today's data that list is empty — keep the type, drop `Guard` from the enum, and let the file be a stub until the next unwritten claim arrives.

`ScorecardLedger.Coverage()` gains one branch on the shape it already walks: `cited` / `judged` / `conditional` / **`guarded`** / exemption / uncovered.

### Benefits

- **Locality** — guard prose lives beside the scenario that produced it. The 16 cross-references become the declaration itself. A commit like `ec64d522` touches one file instead of two.
- **Leverage** — one concept from one type ("a claim is cited, judged, conditional, or guarded") instead of one from `Scenario` and a second from a static table.
- **Testability** — `ClaimCoverageTests.NoClaim_IsBothCoveredAndExcused` exists only because the two lists can contradict each other. With the guard on the scenario that contradiction is unrepresentable and the test is **deleted rather than maintained**. Five dead enum values stop being decoration. Everything stays deterministic and bare-`dotnet test`-runnable — nothing here touches a model.

### ADR position

⚠️ **Touches ADR-0031, does not contradict it.** 0031 says "the uncovered ones sit in an explicit exemption list with a reason" and "the exemption list is the backlog, and it is visible."

A guard is **not an uncovered claim** — a scenario runs and asserts it. This is a distinction 0031 did not draw, not one it settled. Its actual requirements survive: every claim still needs a scenario or a written reason; the backlog stays visible in the scorecard's `coverage` field (which already distinguishes them); a scenario must still be demonstrated red to *cite*.

**Worth a short amending ADR**, because 0031's "either a scenario or a line in the exemption list" phrasing reads literally as today's shape.

### Open questions for grilling

1. Does the guard note belong on the scenario, or on the *claim* with a back-reference? The scenario is where the churn is, but the claim is where the prose is.
2. If `ClaimExemptions` becomes empty, does the type survive at all, or does an empty stub invite the taxonomy growing back?
3. Should the scorecard's `coverage` field keep the `Guard` string for continuity with existing scorecards, or is renaming it fine given `.eval-output/` is gitignored?
4. Is a guard ever legitimately declared for a claim *no scenario names* — i.e. is the residual case truly empty, or just currently empty?
5. Does this land before or after the [tolerance profile](#deferred--scenario-is-wide-but-hold-off)? Both widen `Scenario`.

---

## Candidate 4 — Make the conversation identity a type

**Strength: Strong** · SignalR channel · **contains a live defect**

### Files

| Path | Anchors |
|---|---|
| `McpChannelSignalR/Hubs/ChatHub.cs` | 139, 221, 309, 407 — composes `"{ChatId}:{ThreadId}"` |
| `McpChannelSignalR/Services/StreamService.cs` | 33 (inverse map), **130 (the drop)** |
| `McpChannelSignalR/Services/ApprovalService.cs` | 22, 63 — two more copies of the inverse |
| `McpChannelSignalR/Services/SessionService.cs` | 43 |
| `McpChannelTelegram/McpTools/SendReplyTool.cs` | 118 — `Split(':')` |
| `McpChannelTelegram/McpTools/RequestApprovalTool.cs` | 109 — byte-identical `Split(':')` |
| `Infrastructure/StateManagers/{RedisThreadStateStore,TopicSearchDocuments}.cs` | 432; 158, 254 |

### Problem

One identity, spelled as a string. Verified: **8 production composition sites** (four in `ChatHub` alone) and **4 independent copies of the inverse mapping**, separated by ~40 hops and a process boundary.

The identity **changes name twice on one round trip**:

```
Browser  ──topicId (UUID)──▶  ChatHub
                              :221 discards topicId, composes "chatId:threadId"
         ──conversationId──▶  Agent  (≈40 hops, 4 process boundaries)
         ──conversationId──▶  StreamService
                              :33 maps back to topicId — the exact inverse,
                                  in a different type, in a different file
         ──chunk──▶  Browser
```

`ApprovalService.cs:22` and `:63` each repeat that same inverse independently — a third and fourth copy. The whole round trip is only correct because `SessionService._conversationToTopic` stays alive across it.

**The live defect.** `StreamService.cs:33`:

```csharp
var topicId = sessionService.GetTopicIdByConversationId(conversationId) ?? conversationId;
```

On a miss, this hands a raw `chatId:threadId` to `WriteMessageAsync`, whose lookup can never match it (`_responseChannels` is keyed by topicId). Line 128–131:

```csharp
if (!_responseChannels.TryGetValue(topicId, out var channel))
{
    logger.LogWarning("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
    return;
}
```

The chunk is dropped and **a reply silently vanishes with only a warning**. Reachable today; untested.

### Solution

Let the identity cross signatures as a type carrying both spellings, authored once. The composition and its inverse become its implementation. The `?? conversationId` fallback becomes unrepresentable rather than untested — you cannot hand a conversation-shaped value to a topic-shaped lookup.

### Benefits

- Closes a live silent-drop defect.
- **Leverage** — 8 composition sites become one; 4 inverse mappings become one.
- **Locality** — the round trip reads as one thing instead of an identity that changes name mid-flight.

### ADR position

**No conflict.** ADR-0018 ("a tool call reaches the browser by one route") is working as designed — reply text and tool calls share no type and converge only at `StreamService.WriteMessageAsync`.

### Also in this sweep

**`ReplyContentType.ToolCall` is unreachable.** Verified: 13 occurrences, **zero producers**. The sole production origin is `ReplyDispatcher.cs:52` (`mapped.ContentType`), and `MapResponseUpdate` (80–96) emits only `Text`, `Reasoning`, `Error`, `StreamComplete` — `FunctionCallContent` is explicitly skipped:

```csharp
// FunctionCallContent is intentionally skipped — tool calls are displayed
// by the approval flow (request_approval tool with mode=request or mode=notify)
```

That comment at `ReplyDispatcher.cs:89-91` is the **only** place explaining why the fifth enum member is unreachable — and it sits three projects away from the four `case` arms it invalidates (SignalR `StreamService`, Telegram/ServiceBus/Voice `SendReplyTool`/`ReplySpeaker`). `Domain/DTOs/ReplyContentType.cs` carries no note at all. Nine of the thirteen hits are tests synthesizing the value by hand.

`ToolCallFormatter` is `[PublicAPI]`, which is why no analyzer has flagged it. It has already diverged from its live counterpart in input shape: it parses JSON with PascalCase `"Name"`/`"Arguments"` keys, while `ApprovalService.FormatToolCalls` reads a typed `ToolApprovalRequest` — same output contract, two parsers, one unreachable.

Removing it **completes ADR-0018** rather than reopening it: this is residue from the route 0018 deleted.

**`RegisterAgentsTool` ×5** — SignalR, Telegram, Voice, Scheduling, Library. 87 lines total; a `diff` shows they differ only in namespace and description prose. Small, real, low priority.

**MCP host boilerplate — leave alone.** 5.4% shared ceremony across 14 servers, and the one abstraction anyone would reach for would serve 5 of 14 and be a shallow module.

**Note for the rules file, not a code change.** `SendReplyParams` is destructured to `Dictionary<string, object?>` at `McpChannelConnection.cs:463-472` and reconstructed field-for-field at `SendReplyTool.cs:26-35`, with the `contentType` enum hand-stringified at 467 rather than going through `ChannelProtocol.ToArguments`. The comment at 456–460 justifies this as a hot-path optimization (`send_reply` fires hundreds of times per response) — reasonable — but the consequence is that the wire contract is maintained in two places, against `channels.md`'s claim that `ChannelProtocol` is "the one place the inbound methods and outbound tool names are spelled." Worth documenting.

### Open questions for grilling

1. How far does the type travel? Stopping at the SignalR server is cheap; crossing the MCP wire means the agent side learns it too.
2. Is the `?? conversationId` fallback load-bearing for any real case (a reconnect mid-stream?), or is it purely defensive?
3. Should the drop at `:130` fail loudly rather than warn, independently of the type work — a one-line fix worth doing now?
4. Do the Redis key spellings (`RedisThreadStateStore:432`, `TopicSearchDocuments:158,254`) belong to the same identity, or is that a different concept that merely looks alike?
5. Is deleting `ToolCall` safe against the wire — could an older channel server still send it during a rolling deploy?

---

## Candidate 5 — Give the body window an owner

**Strength: Worth exploring** · Web browsing

### Files

| Path | Lines | Anchors |
|---|---|---|
| `Infrastructure/HtmlProcessing/HtmlProcessor.cs` | 247 | `CreateSuccessResult` 179–229 |
| `Infrastructure/HtmlProcessing/HtmlConverter.cs` | 470 | `Truncate` 54–98, `OverlongEntry` 103–107 |
| `Infrastructure/HtmlProcessing/PageImageEntry.cs` | 145 | `PartialEntryStart` 62–66, `DanglingOpenStart` 73–87, `Count` 89 |
| `Infrastructure/Agents/Mcp/McpImageLift.cs` | 172 | `EnvelopeCandidates` 159–160 |
| `Domain/Agents/ReadImageHydration.cs` | 271 | `SplitEnvelopes` 101–115, `StoreKey` 120–121 |

### Problem — two instances of one shape

**(a) The body-window coordinate system is spread over three modules with no owner.**

`HtmlProcessor.CreateSuccessResult` strips control chars (185), slices the offset (189–194), counts entries *after* the slice and *before* truncation (201), calls `HtmlConverter.Truncate(content, maxLength, out consumed)` (207), counts again (213), and computes `NextOffset = offset + consumed` (227). `Truncate` owns the 70%-newline back-up (72–75), the entry back-up (81–90), the overlong escape hatch (87), and "measure `consumed` after the trim, unless the trim emptied it" (95–96). `PageImageEntry` owns two different partial-entry detectors.

Every comment in that method is a scar from one of the fixes. The invariant nobody owns:

> *offset + consumed lands on a real position in the same coordinate space the caller's offset was in, and the window between them contains exactly `ImageCount` whole entries with `ImagesBeyondWindow` ahead.*

Four fixes are exactly this — `602bad2a` (counting past the offset), `eddb2104` (cut inside an opening), `c9574866` (nextOffset measured, not inferred), `2fcd76fd` (a window always advances) — each fixed in whichever of the three files it surfaced in. Nothing tests the *composition* as a paging loop that must terminate.

**(b) The picture-index rule is written twice, on purpose, and both statements are prose-guarded.**

`McpImageLift.cs:159-160`:
```csharp
// Anything hydration's own split will count as a candidate. It has to be the same question,
// asked the same way, or the two sides number the pictures differently again.
public static IEnumerable<string> EnvelopeCandidates(string text) =>
    text.Split("\n\n").Where(part => part.TrimStart().StartsWith('{'));
```

`ReadImageHydration.cs:101-105`:
```csharp
private static IEnumerable<PageImageResult?> SplitEnvelopes(string text) =>
    text.Split("\n\n")
        .Select(part => part.Trim())
        .Where(part => part.StartsWith('{'))
```

Different assemblies, must answer identically, held together by a comment. `6fa557da` is the commit where they disagreed and **every image landed one key off its bytes** — each hydrating as "no longer in view". `McpImageLift.KeyFor` and `ReadImageHydration.StoreKey` also spell the `#` separator independently (a `const char` on one side, a literal in an interpolated string on the other).

### Solution

**(a)** One module taking the full markdown, the offset and the maxLength, answering the window plus its four facts (`contentLength`, `nextOffset`, `imageCount`, `imagesBeyondWindow`), with `Truncate`'s back-up rules and the entry detectors as private detail. `HtmlProcessor` produces markdown and asks for a window; it stops doing arithmetic. `Truncate` stops being a public utility with an `out` parameter only one caller understands.

**(b)** Put the paragraph-split-and-index rule in `Domain` — it is a pure text rule with no infrastructure in it — and have `McpImageLift` call it, so both sides ask literally the same function.

⚠️ **The direction is forced**: `ReadImageHydration` is in `Domain`, `McpImageLift` is in `Infrastructure`, and Domain may not reference Infrastructure. `Domain` already owns `ImageRef`/`ElementRef` for exactly this reason — *"One definition, because two would be two answers to 'is this mine?'"*

### Benefits

- **Locality** — paging becomes readable in one file. "Where does the next window start" stops being an identity you verify by reading two files side by side.
- **Tests** — the property that actually matters becomes expressible: **a paging loop terminates and visits every entry exactly once**, as one property test over generated bodies, replacing eleven example-based facts split across two files. For (b), `PageImageRoundTripTests` stops being the only thing preventing drift, because drift becomes unrepresentable.

### ADR position

**No conflict.** ADR-0033's four decisions — nextOffset measured by the truncation rather than inferred, an entry never split, a window always advances, the index counts `{`-paragraphs — all survive verbatim. Only which module states them changes.

⚠️ 0033's "measured by the truncation itself after the trim" is a decision about *where the measurement happens* — the new module must keep it at the cut, not recompute it.

### Open questions for grilling

1. Is the body window one module or two — the offset/truncate arithmetic and the entry counting are related but separable.
2. Does the paragraph-split rule in `Domain` need a name in `CONTEXT.md`? It is currently a nameless shared invariant.
3. Property test over generated bodies — is there an existing generator convention in this repo, or would that be a new dependency?
4. Why is this "Worth exploring" and not Strong? Mainly because the fixes have slowed; is the coordinate system now actually settled?

---

## Deferred — `Scenario` is wide, but hold off

**Eval harness.** `Tests/Eval/Harness/Scenario.cs` (270 lines).

Verified: **22 members** on `Scenario` (5 required), twelve further records in the same file, **18 types reachable** from a scenario literal, ~36 declaration lines per scenario across 61 scenarios. `CallPermission` is written by hand **81 times**:

| Family | Literals |
|---|---|
| Vault | 25 |
| Web | 20 |
| Home Assistant | 13 |
| Mount | 11 |
| Mechanism | 8 |
| Delegation / Voice | 2 each |

The harness already noticed — `CallPermission.Looking` and `LookingAndManuals` exist, each with a comment saying in effect *this exists because a scenario that spells it out by hand will miss a line and report a glob as a behavioural failure*. But `WebScenarios` reaches for them **zero** times while writing 20 literals.

**A drift already exists.** `MayDelegateTo` appears 11 times, `WorkerAnswer` 9 — so `MountScenarios` and `VoiceScenarios` allow delegation while taking the default `WorkerAnswer`, which is a *success* string (`"Hecho. Te dejo el resultado resumido en dos líneas."`). `WebScenarios` declares its own `WorkerCannotBrowse` precisely because the default is harmful for its family. Two fields always set together, whose correct combination is documented in a comment in a third file, is a shallow interface.

**Proposed shape:** a named **tolerance profile** — the ambient permissions and delegation tolerance a *family* shares ("vault work", "web work", "action-file work"). A scenario names one and declares only what distinguishes it from its siblings. `MayDelegateTo` + `WorkerAnswer` become one thing.

**Why deferred:** it widens the same type as [candidate 3](#candidate-3--move-the-guard-onto-the-scenario). Do that first.

⚠️ **ADR-0030 constraint:** its rejection of a forbidden list turns on the permitted set being **exhaustive**. A profile must be a named *set* of permissions, never a wildcard — no profile may contain a `*` tool pattern. Worth a test alongside the existing `APermittedToolOnADifferentPath_Fails`.

**Do not collapse** `Required` / `Ordering` / `Changes` / `Files` / `Remembered` / `Reply` / `Judged` — those are genuinely independent questions about a recording.

---

## Checked and working — leave alone

| Module | Why it is healthy |
|---|---|
| **`ScenarioChecks`** | 368 lines behind a two-argument function; ten independent checks composed in a ten-line table at 15–27. The deepest module in the suite. `EvalSuite.RunAsync` is 57 lines; `ScenarioRunner.RunAsync` is 80 implementing the whole k-of-N policy behind `(policy, run) → ScenarioResult`. **Do not split.** |
| **`ModalDismisser`** | 638 lines, one entry point (`DismissModalsAsync`), a real result type, tests driving it through that one entry point. Took four fixes — **every one a policy fix inside it, none a sequencing fix at its caller**. That difference is what this whole review is about. |
| **`IWebBrowser`** | 6 methods, 4 adapters (including `RecordingBrowser` and the eval stack's). A real seam; the tools test cleanly through it via `Mock<IWebBrowser>`. The `protected RunAsync` + `[McpServerTool]` subclass split is the seam that lets Domain tests run without MCP. |
| **The eval harness's own tests** | `ScenarioChecksTests` (714 lines, 44 cases) drives `ScriptedTurn` into a real `Recording` and calls the same entry point the runner calls. `EvalWebTests` goes through the real browser and Brave client. No test reaches around the seam — exactly what ADR-0030 asks for. |
| **`IsWarmUpProbe`** | A model tic encoded in a check, but its comment already reasons it onto `maxResults <= 1` rather than query wording. Correct as-is. |

**Minor, documented as accepted, not worth a candidate:** `ViewImageTool.MaxPerCall` spelled three times (constant, tool description, MCP parameter description); `AccessibilitySnapshotService.CaptureAsync`'s parameter list exposing stamping mode as three orthogonal flags (would be absorbed by candidate 1); `sessionId` passed to `CaptureAsync` and apparently unused by the script.

---

## Suggested order

1. **[Candidate 1](#candidate-1--give-the-tab-protocol-a-module)** — largest module, hottest churn, bugs the tests structurally cannot catch, and ADR-0034 already asserts the shape.
2. **[Candidate 2](#candidate-2--one-label-ladder-two-renderers)** — shares files with 1, and the interpolation mechanism it needs already works in that same JS block.
3. **[Candidate 4](#candidate-4--make-the-conversation-identity-a-type)** — independent, parallelisable, smallest of the five, and closes a live defect. The one-line "fail loudly at `:130`" fix could land immediately.
4. **[Candidate 3](#candidate-3--move-the-guard-onto-the-scenario)**, then the [tolerance profile](#deferred--scenario-is-wide-but-hold-off) — same type, so sequence them.
5. **[Candidate 5](#candidate-5--give-the-body-window-an-owner)** — grill first; the fixes in this area have slowed and it may already be settled.
