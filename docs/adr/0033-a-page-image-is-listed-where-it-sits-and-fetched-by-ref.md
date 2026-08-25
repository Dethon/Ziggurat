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

**A page image is listed where it sits in the page, and fetched by ref through the session that
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
failing to find it, and `view_image` can do the same with `e-3`. They live in the browser session
and die with it, at the same thirty-minute idle the page does. A ref that outlived its session
refuses and says to browse the page again; keeping a second, longer clock for image refs alone
would mean two expiry rules on one page, differing invisibly.

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
last newline when the cut lands past seventy percent of target; it now also backs up past a
partial image entry, so the body always ends on a whole one and every ref the model can see is a
ref it can use. Beyond the window the envelope names how many images remain, because an image the
model cannot see is one it cannot know to page forward for.

**Every refusal says which wall it is.** No vision on the model, a ref from a dead session, a ref
that was never an image, a fetch the site refused, an image past the per-call cap, bytes already
forgotten — six sentences, not one. `file_read` already works this way, and `AttachmentCapability`
goes as far as naming the model in the sentence. A model told only "unavailable" either retries a
permanent failure or abandons a retryable one.

## Consequences

- Every browse result grows by an entry per surviving image, whether or not the turn ever fetches
  one. The filter and the terse entry shape are what keep that small; a heavily illustrated page
  still pays.
- Images consume the pagination budget, so the text window a given `offset` and `maxLength` return
  shifts on image-heavy pages. `contentLength` remains the pre-slice total, so paging arithmetic
  is unchanged.
- A count cap cannot stop a large-byte call. Eight full-size images pass the cap, pass truncation's
  per-image estimate, and can still fail as an oversized request. Accepted on the same terms as
  ADR 0029's parallel reads: the model asked for those pictures.
- Refs die with the session, so an image the model wants again half an hour later costs a fresh
  browse of the page. The alternative was carrying URLs in the model's context for every image on
  every page against the chance that one is wanted late.
- `ToolResponse` grows an image result, and `QualifiedMcpTool` stops being a pure pass-through. It
  is now the seam where an MCP image becomes a read image, which is a behaviour every MCP server
  in this repo inherits without opting in.
- `StripDomNoiseAsync` keeps `src` for images that pass the size filter, so the DOM the markdown is
  built from is no longer uniformly image-free.
- The size filter needs rendered dimensions, not markup attributes, so listing images asks the page
  a question that reading its text did not.
- No eval scenario. The mechanism is unit-testable end to end and the fetch is covered against the
  real container; whether a model chooses to look at a picture when it should is a behavioural
  claim nobody has yet made a prompt claim about.
