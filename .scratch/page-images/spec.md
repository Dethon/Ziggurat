# Page images in web browsing

Status: implemented

## Problem Statement

The agent browses a page and is told a picture is there, but can never look at it. Ask it what
a chart shows, what a scanned notice says, or what the product in a listing actually looks like,
and the best it can do is repeat the alt text somebody else wrote — or say nothing, because most
images carry no alt text at all.

This is not a gap in what the agent can see. It already reads images off a mount: `file_read`
hands one back and the model looks at it. The gap is that the one place images actually live —
the web — is the one place the agent is blind, and the reason is a single line of cleanup that
throws away the image's address while carefully keeping its caption.

## Solution

A browsed page lists its pictures where they sit, and the model asks for the ones it wants.

Each image that survives a size filter gets an entry in the markdown body, right where the image
occurs in the text: a short handle and the best name the page gives it. The model reads the page,
sees a picture is there and roughly what it is, and calls `view_image` with the handles it cares
about. The pictures come back through the same browser session that found them, and the model
sees them the same way it sees an image read off a mount.

Position carries meaning. A picture under a heading is about that heading, and a list of eleven
images at the bottom of a result destroys the only signal the model has for choosing between them.

## User Stories

1. As a person asking about an article, I want the agent to describe the photograph in it, so that I learn what the article is actually showing me and not just what it says.
2. As a person asking about a chart on a page, I want the agent to read the chart, so that I get the numbers even when the page publishes them only as a picture.
3. As a person asking about a product listing, I want the agent to look at the product photo, so that I can ask about colour, shape or condition rather than trusting the seller's description.
4. As a person reading a scanned document online, I want the agent to see the scan, so that a page with no machine-readable text is still useful to me.
5. As a person browsing a recipe, I want the agent to see the finished dish, so that I can ask whether it looks like what I am trying to make.
6. As the model, I want each image listed where it occurs in the page text, so that I can tell which picture belongs to which section rather than guessing from a flat list.
7. As the model, I want an image's entry to carry the best label the page offers, so that I can decide whether the picture is worth asking for before I pay to see it.
8. As the model, I want an image with no alt text to still be listed with something — a caption, a title, a link's text, a filename, or failing all of those its size — so that an unlabelled picture is not invisible.
9. As the model, I want spacers, icons and tracking pixels left out entirely, so that the list I read is pictures and not page furniture.
10. As the model, I want image handles spelled differently from the handles I use to click things, so that I can tell at a glance which tool a handle was meant for.
11. As the model, I want to ask for several images in one call, so that comparing pictures does not cost me a round trip each.
12. As the model, I want a call that asks for too many images to return the ones it can and name the rest, so that being greedy costs me a follow-up call rather than everything.
13. As the model, I want images fetched through the session that listed them, so that a picture served only to a browser with the right cookies still reaches me.
14. As the model, I want to be told specifically why an image did not arrive, so that I can tell a failure worth retrying from one that never will.
15. As the model, I want to be told when the model I am running on cannot accept images at all, so that I stop asking rather than failing repeatedly.
16. As the model, I want a handle from a session that has since closed to say so plainly, so that I know to browse the page again instead of concluding the image is gone.
17. As the model, I want an image I saw earlier and can no longer see to leave a note saying I can ask again, so that a picture leaving my view is not the same as a picture being lost.
18. As the model, I want the page body to never end mid-entry, so that every handle I can read is a handle I can use.
19. As the model, I want to be told how many images lie past the part of the page I was given, so that I know whether paging forward would show me more pictures.
20. As the person paying for the conversation, I want images to stop being sent back once they fall out of view, so that one look at a picture is not billed on every turn for the rest of the conversation.
21. As the person paying for the conversation, I want a page's image list to stay small on pages full of decoration, so that browsing does not get more expensive for no benefit.
22. As an operator, I want a browsed image's bytes to never enter the conversation history, so that reading a history costs the same whether or not pictures were ever looked at.
23. As an operator, I want the browse server to keep knowing nothing about the agent's storage, so that the two remain separately deployable.
24. As an operator, I want a host with no image store to keep working exactly as it does today, so that adding this feature cannot break a deployment that does not want it.
25. As a developer, I want any future MCP server that returns an image to get eviction for free, so that the next producer does not have to rediscover this design.
26. As a developer, I want the vocabulary for a browsed picture to be the same vocabulary used for one read off a mount, so that there is one story about images and not two.
27. As a developer, I want the page-text extraction to stay testable without a browser, so that label and position rules can be checked in milliseconds.

## Implementation Decisions

### The catalogue

- `web_browse` writes an **image entry** into the markdown body at the position the image occupies in the document, carrying an **image ref** and a label. The entry is terse — the always-on cost is paid by every browse, including those that never fetch.
- Labels resolve by falling back: alt text, then `figcaption`, then `title`, then the text of an enclosing link, then the filename. An image surviving all five is listed with its rendered dimensions.
- Images under roughly 100px on either side get no entry and no ref, and are unreachable. The bound is on **rendered** dimensions, not markup attributes, which lie.
- **The page annotates before extraction.** `PlaywrightWebBrowser` stamps measured dimensions onto surviving `img` elements before handing HTML to the processing layer, which then reads plain attributes. This keeps extraction a pure function of an HTML string and puts the one browser-dependent step behind an integration test.
- DOM noise stripping keeps `src` for images that pass the filter and strips it for everything else, as today.

### Refs

- Image refs occupy their own namespace, spelled distinctly from the element refs the accessibility snapshot assigns. A tool handed the wrong kind refuses it by name rather than failing to find it.
- Image refs live in the browse session and expire with it. A ref from a closed session refuses and says to browse the page again. No second, longer-lived clock.

### `view_image`

- Accepts a list of refs, capped at 8 per call. Over the cap: fetch the first 8, name the rest in the envelope. Partial success.
- The cap counts images, not bytes. This is a deliberate weaker guard — see Further Notes.
- Fetches through the live browser page so cookies, referer and fingerprint apply.
- Returns a protocol image block. The browse server gains no knowledge of the agent's storage, conversation ids or tool call ids.

### The bridge

- The MCP-to-agent bridge lifts image bytes out of a tool result, writes them to the agent's read-image store under the same conversation-and-call key the filesystem read tool uses, and substitutes a text envelope. Nothing downstream of the bridge sees bytes, so conversation history stays text-only.
- **The store is constructor-injected and nullable.** Null means no lifting and bytes pass through as today, so a host without a store degrades to current behaviour rather than failing.
- Lifting happens where the conversation context is already resolved — the same place the bridge builds its conversation meta — not in the existing pure flattening helper, which has no access to ids or storage.
- Stripping at the history writer was rejected: the live turn and the stored turn would disagree. Stripping in the chat client was rejected: it sits downstream of persistence on some paths.

### Hydration

- A page image **is** a read image. Same store, same 20-conversational-message distance, same forget-on-exit, same placeholder shape. Hydration is not told which tool produced the image.
- The recogniser must accept the browse envelope as well as the filesystem one.

### Truncation

- Body truncation never cuts an image entry in half — it backs up past a partial entry the way it already backs up to a newline.
- The envelope reports how many images lie beyond the returned window.

### Refusals

Six distinct messages, each naming its wall: no vision on the model; ref from a dead session; ref that was never an image; site refused the fetch; past the per-call cap; bytes already forgotten. Prior art: the filesystem read tool's refusal set, and the attachment capability refusal that names the model in its sentence.

## Testing Decisions

A good test here asserts what a caller can observe — the markdown a page produces, the string a truncation returns, the result an invoked tool hands back, the bytes a store ends up holding. None should reach for internal state or assert on call order.

**Four seams, three already existing.** Fewer would mean testing pure string rules through Docker.

1. **Page-text extraction** (existing unit tests). HTML string in, processing result out. Covers entries appearing at the right position, the full label fallback chain, and the size filter — reading annotated attributes exactly as production will, because the page stamps them before extraction.
2. **Truncation** (existing unit tests). A pure string function. Covers "never split an entry" exhaustively, including an entry straddling the cut and an entry at the very boundary.
3. **The MCP bridge** (new, and the only new seam). Construct the bridge tool with a fake store and a stub inner tool returning an image block; assert the bytes reach the store under the expected key and the returned result carries envelope text and no bytes. Also: a null store passes the block through unchanged. Prior art: the existing read-image tests around the chat client.
4. **Browser integration** (existing integration tests, real Camoufox). Dimension annotation against a real rendered page, and fetching an image through a live session. This is the layer that cannot be honestly faked.

Refusal messages are unit-tested at whichever of the above seams produces them, one test per wall.

**No eval scenario.** The mechanism is unit-testable end to end; whether a model *chooses* to look at a picture when it should is a behavioural claim, and no prompt claim has been declared for it.

## Out of Scope

- **Page screenshots.** Rendering a picture of the page itself is a different feature with a different justification. Nothing here forecloses it, and the bridge seam would serve it unchanged.
- **Inlining images automatically.** Pictures arrive only when the model asks by ref. A page's images never enter the context unbidden.
- **Fetching an image by bare URL.** Refs were chosen as the handle; a URL path would exist only for images the browse tool never listed, which is a separate need.
- **Sending images onward to the person.** This is about what the model can see, not what gets delivered to a channel.
- **Re-encoding, downscaling or format conversion.** An image is fetched as served or refused, matching the existing decision for images read off a mount.
- **Surviving session expiry.** A ref outliving its page was considered and declined.
- **A byte-based cap.** Considered and declined in favour of a count.
- **Video, audio and PDF on a page.** Images only.

## Implementation Notes

Two decisions taken during implementation, both confirmed with the user:

- **Several pictures from one call are keyed `<callId>#<n>`**, rather than widening
  `IReadImageStore`'s one-image-per-key contract that `file_read` depends on. `n` counts JSON blocks
  in the result, not pictures — the call's own envelope leads every result and takes index 0. The
  bridge and hydration compute it independently, so `PageImageRoundTripTests` drives both halves
  over a real result to stop them drifting.
- **The no-vision refusal is made agent-side.** The capability catalogue and the turn's resolved
  model live hub-side, so the browse server cannot answer the question; hydration refuses with the
  note instead. The cost is that a vision-less turn still fetches and stores the bytes.

Two bugs the tests caught that the spec did not anticipate:

- Measurement had to move ahead of `<style>` removal. Stripping stylesheets first makes a
  CSS-sized image measure at its intrinsic size — zero while its bytes are in flight — so every
  such content image filtered out as furniture.
- A picture inside a link was swallowed whole: a link renders from its text, which no image
  contributes to, and product photos are linked more often than not.

## Further Notes

Three risks were accepted knowingly rather than overlooked, and are recorded in the ADR:

- **A count cap cannot stop a large-byte call.** Eight thumbnails and eight full-page photographs are treated identically, and only the second can trouble a context window. A count is what the model can reason about before calling; bytes are the truer bound. Same posture as the existing accepted risk around parallel image reads.
- **Entries consume the pagination budget.** They sit in the text that paging slices over, so an image-heavy page shifts the text window a given offset returns. The pre-slice total length is unchanged, so paging arithmetic still works.
- **Refs expiring with the session** means wanting an image again half an hour later costs a fresh browse. The alternative was carrying image URLs in context on every page against the chance one is wanted late.

The decision record is `docs/adr/0033`. It amends `docs/adr/0029`'s assumption that an image producer runs in the agent's own process — that was true of every producer existing when it was written.

Glossary terms this feature introduces or widens live in `CONTEXT.md`: **read image** (widened past mounts), plus a new web browsing section defining **browse session**, **element ref**, **image ref** and **image entry**. Element ref predates this work but had never been written down, and image ref could not be defined against it otherwise.

Implementation-facing constraints are in `.claude/rules/web-browsing.md`, loaded whenever this code is touched.
