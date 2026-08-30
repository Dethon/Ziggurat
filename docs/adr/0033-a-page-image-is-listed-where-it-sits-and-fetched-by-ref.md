# 0033 — A page image is listed where it sits and fetched by ref

Status: accepted
Date: 2026-08-25

## Context

The browse tools read pages as text. `StripDomNoiseAsync` drops `script`, `style`, `iframe` and
`svg` outright, and for an `img` it strips `src`, `srcset` and `data-src` and keeps only the alt
text. So the model already sees that a picture is there and is told roughly what it shows, and has
no way whatsoever to look at it: the one attribute that could reach the bytes is the one thrown
away. A chart, a scanned notice, a photograph of the thing the page is about — all of it reaches
the model as a caption written by whoever built the page, or as nothing at all.

Showing the model an image is a solved problem here. ADR 0029 put the chat client on the Responses
wire precisely so a tool result may carry a picture, and built the rest around it: a text envelope
in the history, bytes in a store of the agent's own keyed by conversation and tool call, one
hydration pass that swaps the envelope for `[text, picture]` on the way out and forgets the bytes
once the reference leaves view, capability asked at both ends, and a token estimate that counts an
image as an image. Nothing in that machinery is about mounts. It is about a reference the model
put there itself.

What is new is where the tool runs. `file_read` is a Domain tool executing in the agent's own
process, so it writes to `IReadImageStore` directly. The browse tools are not: they live in
`McpServerWebSearch`, a separate container that reaches the agent only across MCP and knows
nothing of Redis, of conversation ids beyond what `ConversationScope` hands it, or of tool call
ids at all. ADR 0029's "the bytes rest with the agent" was true of every image producer that
existed when it was written, and quietly assumed the producer was hub-side.

The MCP protocol has an answer — a result block may be an image — and the bridge already tolerates
one: `QualifiedMcpTool.Flatten` joins a multi-block result into a single string only when every
block is text, and otherwise passes the block list through as `AIContent`. So an image would
survive the crossing today. It would then be serialized into the history list along with the rest
of the message, because a turn is stored as `JsonSerializer.Serialize(chatMessage)` — and a
`DataContent` serializes as base64 text. That is ADR 0029's rejected outcome reached by a
different road: a picture the model can no longer see, costing its full base64 weight on every
subsequent turn, forever.

## Decision

**A page image is listed where it sits in the page, and fetched by ref through the tab that
found it.**

`web_browse` writes each surviving image into the markdown body at the point it occurs, as a ref
and a label. Position is content: a picture under a heading is about that heading, and a
catalogue in the envelope would say a page has eleven images while destroying the only thing that
tells the model which one to want. The cost is that images share the body budget that `maxLength`
and `offset` paginate over, which the truncation rule below pays for.

**An image too small to be about anything is not listed and has no ref.** Under roughly a hundred
pixels on either side, measured as the page actually rendered it rather than as the markup claims,
an image is a spacer, an icon, a tracking pixel or a bullet. Dropping them is not a token
optimisation dressed up as a filter: an unreachable favicon costs the model nothing, and a
catalogue where nine entries in ten are 1×1 is a catalogue nobody reads. A page whose content is
genuinely tiny images loses to this, and that is accepted.

**Labels fall back rather than going blank.** Alt text, then a `figcaption`, then `title`, then
the text of an enclosing link, then the filename; an image that survives all five with nothing to
say is listed with its rendered dimensions. A list of entries called "image" is not a menu, and
dimensions alone still separate a photograph from a logo.

**Image refs are their own namespace.** `i-1`, `i-2`, beside but never inside the `e-` refs the
accessibility snapshot assigns. One page, two kinds of handle, because a ref's shape is what tells
a tool whether the request was meant for it: `web_action` can refuse `i-3` by name instead of
failing to find it, and `view_image` can do the same with `e-3`. Numbers are session-unique —
each namespace counts monotonically across every tab and every snapshot, so a number is never
reused and a stale ref refuses instead of resolving against whatever page came later. A ref lives
in the tab that stamped it and dies when that tab closes, reloads or renumbers — and with the
whole session, at the same thirty-minute idle; a ref that outlived its page refuses and says to
browse it again. Keeping a second, longer clock for image refs alone would mean two expiry rules
on one page, differing invisibly.

**`view_image` takes a list, capped at eight, and partial success is success.** Comparing pictures
is the ordinary case and should not cost eight round trips. Over the cap, the first eight are
fetched and the rest are named in the envelope, so a greedy call makes progress and learns the
rule instead of bouncing off it. The cap counts images, not bytes — eight thumbnails and eight
full-page photographs are treated alike, and only the second can trouble a context window. Bytes
are the truer bound and a count is the one the model can reason about before it calls; the risk is
recorded below rather than defended.

**The fetch goes through the page that listed it.** Camoufox holds cookies, referer and a
fingerprint, and a great many images are served only to a request that carries them; a bare
`HttpClient` would answer 403 or a placeholder pixel on exactly the pages where the browsing stack
is earning its keep. This is also what makes the ref meaningful — it resolves against the live
page, so a URL never has to enter the model's context to be usable.

**Bytes as served first, pixels only when no fetch can obtain them.** A page's pictures usually
come from somewhere else: `commons.wikimedia.org` serves its images from `upload.wikimedia.org`,
`unsplash.com` from `images.unsplash.com`. Both origins therefore start the same way, with a wire
`fetch` that keeps the served bytes byte for byte — same-origin with the page's credentials,
cross-origin anonymously (`credentials: 'omit'`). Anonymously, because the trap here is not CORS
in general but credentials in particular: the big image CDNs do send
`Access-Control-Allow-Origin: *`, and a wildcard is exactly what a credentialed request is
rejected against, so a `credentials: 'include'` fetch fails on precisely the images the page is
displaying perfectly well, in the misleading shape of the site refusing. Only wire rasters
(png/jpeg/gif/webp) leave as served; an as-served `image/svg+xml` — Wikipedia's own site logo is
one — makes the vision provider reject the entire request as an unsupported media type, which
costs the turn rather than the picture.

Where the wire fetch fails or answers a non-raster, the canvas runs: load the image anonymously
(`crossOrigin='anonymous'`, usually a cache hit on pixels the browser has already decoded), draw
it, read the pixels back as PNG. That anonymous load is gated by the same
`Access-Control-Allow-Origin` header the anonymous `fetch` was, so a CDN sending none fails the
probe too, and falling back to the rendered element taints the canvas.

A tainted canvas is not a refusal, because CORS binds script inside the page and nothing else. It
answers with the image's address instead, and two rungs remain: re-request that address through
the context's own request client (same cookies, no CORS, bytes as served, wire rasters only),
and failing that — a host that blocks non-browser clients, a non-raster answer — take a Playwright
element screenshot of the pixels the compositor has already painted, reading the rendered box back
as PNG at the size the page shows it. The rung order is not arbitrary: each step down trades
fidelity for reach, so the picture arrives as the file the site served whenever that is possible at
all, and as pixels only when nothing else can get it. What remains a real refusal is an image the
browser itself will not show.

**The MCP server answers with an image block, and the bridge lifts the bytes out.**
`McpServerWebSearch` returns what the protocol says an image result is, and does not learn Redis,
conversation keys or tool call ids. `QualifiedMcpTool` — the one place a tool result crosses from
MCP into the agent — takes the `DataContent` out, writes it to `IReadImageStore` under the same
`(conversation, call)` key `file_read` uses, and substitutes the envelope. Nothing downstream of
the bridge ever sees the bytes, so the history stays a list of text, and every future MCP server
that returns an image gets eviction and hydration without knowing they exist. Stripping at the
history writer instead was rejected: it would leave the running turn and the stored turn
disagreeing about what happened. Stripping in the chat client was rejected because it sits
downstream of persistence on some paths, so the bytes would reach Redis first on exactly the paths
that matter.

**Hydration is not told which tool produced the image.** A browse image is a read image: the model
asked for it, it rides in a tool result, its bytes rest with the agent on a clock of their own,
and it can be asked for again. It gets the same twenty-message distance, the same forget-on-exit,
the same placeholder shape. The glossary's definition widens to match — the mount was incidental,
and what defines a read image is that nobody sent it.

**An entry is never split, and the envelope counts the rest.** Truncation already backs up to the
last newline when the cut lands past seventy percent of target; it also backs up past a partial
image entry, so the body ends on a whole one and every ref the model can see is a ref it can use.
Beyond the window the envelope names how many images remain, because an image the model cannot
see is one it cannot know to page forward for.

**A window always advances, even when that costs the budget.** An entry longer than the whole
window has nothing to back up to, and backing up to nothing consumes zero — a model paging by
what it was told would page the same offset forever while `imagesBeyondWindow` kept promising the
entry ahead. An oversized entry therefore goes out whole and over budget. The cap is a safeguard,
not an editor, and a window that consumes nothing is a page paging can never get past.

**Paging is by the offset the envelope names, not by the body's length.** Because the cut backs up
and the body carries a suffix, the body's length says nothing about where the next window starts.
A truncated envelope therefore carries `nextOffset`, measured by the truncation itself after the
trim rather than inferred afterwards, with control characters stripped before any offset
arithmetic so every position shares one coordinate space. Paging by `maxLength` instead would skip
exactly what the back-up left — including the entries `imagesBeyondWindow` had just promised.
`contentLength` remains the pre-slice total, so the arithmetic for "how much is there" is
unchanged; what changes is that the model is told where to resume rather than computing it.

**Every refusal says which wall it is.** No vision on the model, a ref from a dead session, a ref
that was never an image, a fetch the site refused, a dead-link image whose bytes never arrived, an
image past the per-call cap, bytes already forgotten — seven sentences, not one. `file_read`
already works this way, and `AttachmentCapability` goes as far as naming the model in the
sentence. A model told only "unavailable" either retries a permanent failure or abandons a
retryable one.

The dead link earns its own wall rather than folding into the site's refusal, because the two
recoveries are opposites. A page whose inline images point at a host that has since stopped
serving them — a 2013 article whose image host now answers 405 — lays out the boxes while the
bytes never arrive; telling the model the site refused invites a retry that can never work and
promises a picture that is not there. `SiteRefused` therefore narrows to a CDN guarding pixels the
page is visibly displaying, which is worth retrying, and a dead link says to pick a different
picture.

**The no-vision wall is answered agent-side.** The capability catalogue and the turn's resolved
model both live hub-side, so `McpServerWebSearch` cannot answer the question at all: it reports
the model accepts images and hydration leaves the note instead. `ViewImageTool` still implements
and tests the refusal, which is what a direct domain-tool caller gets. The cost is that a
vision-less turn fetches and stores bytes nobody will see — accepted, because the alternative is
teaching the browse server a capability catalogue it has no business holding.

Only three of the seven walls are states of a fetch — a ref that names no image, a site that
refused, a dead link. A dead session and a ref past the cap are answered before any fetch is
attempted; no vision and forgotten bytes are answered agent-side, after the result has crossed
back. The fetch's own status type therefore carries those three and no more (alongside success,
and later the two stale-ref walls, which are also decided at fetch time). A status nothing can
produce is a refusal nobody can receive, and inventing one would invite a caller to switch on a
case that never arrives.

## Consequences

- Every browse result grows by an entry per surviving image, whether or not the turn ever fetches
  one. The filter and the terse entry shape are what keep that small; a heavily illustrated page
  still pays.
- Images consume the pagination budget, so the text window a given `offset` and `maxLength` return
  shifts on image-heavy pages. `contentLength` remains the pre-slice total, but a caller computing
  its next offset from the body's length is wrong wherever the cut backed up, which is why the
  envelope names that offset instead.
- An oversized image entry defeats the budget rather than the budget defeating it: a window is
  allowed to overrun so that it always advances.
- The label ladder exists twice — once building the entry, once naming the picture in the note
  that replaces it — and the two must be changed together. Nothing enforces it but a test.
  *Retired 2026-08-30*: decided that the in-page copy goes. The fetch script answers only
  facts — the strings the page offers about a picture — and the one ladder in
  `PageImageEntry` names both the entry and the note, so no second copy is left to change
  together. The rung order and the credentials rule decided above are untouched.
- A count cap cannot stop a large-byte call. Eight full-size images pass the cap, pass truncation's
  per-image estimate, and can still fail as an oversized request. Accepted on the same terms as
  ADR 0029's parallel reads: the model asked for those pictures.
- Refs die with their tab — closed to make room, reloaded, or idled out with the session — so an
  image the model wants again later costs a fresh browse of the page. The alternative was carrying
  URLs in the model's context for every image on every page against the chance that one is wanted
  late.
- `ToolResponse` grows an image result, and `QualifiedMcpTool` stops being a pure pass-through. It
  is now the seam where an MCP image becomes a read image, which is a behaviour every MCP server
  in this repo inherits without opting in.
- `StripDomNoiseAsync` keeps `src` for images that pass the size filter, so the DOM the markdown is
  built from is no longer uniformly image-free.
- A cross-origin image whose CDN sends `Access-Control-Allow-Origin` arrives byte for byte as
  served. Only the canvas and screenshot rungs re-encode, and what they carry is pixels rather
  than the original file, so that cost falls only on images no fetch could obtain.
- The size filter needs rendered dimensions, not markup attributes, so listing images asks the page
  a question that reading its text did not.
- No eval scenario. The mechanism is unit-testable end to end and the fetch is covered against the
  real container; whether a model chooses to look at a picture when it should is a behavioural
  claim nobody has yet made a prompt claim about.
