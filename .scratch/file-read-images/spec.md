# Read images through the filesystem read tool

Status: ready-for-agent

## Problem Statement

A person keeps images where they keep everything else: a screenshot in the vault, a scanned page
in the sandbox, a cover in the media library, a photo on their own machine behind an outpost. They
ask the agent about one, and the agent cannot look at it. The filesystem read tool reads text, so
an image is either refused or decoded as mojibake, and the answer comes back as a guess from the
filename.

The agent can already see images the person attached to a message — hydration puts an attachment's
bytes in front of the model — so the gap is arbitrary from where the person sits. The same picture
is visible if they send it and invisible if they tell the agent where it is. Worse, once a file has
been landed in the sandbox or fetched by a download, the agent that put it there can no longer see
it.

## Solution

The read tool learns images. It is renamed `file_read`, because a tool that reads both is not
`text_read`, and it keeps doing exactly what it does today for text.

Asked for an image, it answers a small envelope naming the path, the media type and the size, and
the image itself arrives in front of the model as its own message immediately afterwards. The
person asks "what does the error in that screenshot say" and the agent reads it.

An image the model reads is not an attachment: nobody sent it, the model asked for it. It arrives
attributed to the system rather than as part of what a person said, it stays in view for the same
distance an attachment does, and when it drops out of view the model is told which image it lost
and that reading the file again will bring it back — true, because the file never left the mount.

## User Stories

1. As a person talking to the agent, I want it to read an image in my vault, so that I can ask
   about a screenshot without re-sending it as an attachment.
2. As a person talking to the agent, I want it to read an image on my own machine through an
   outpost, so that I do not have to copy files around to ask a question about them.
3. As a person talking to the agent, I want it to look at a file it just landed in the sandbox, so
   that the attachment I sent is as useful to it after landing as it was before.
4. As a person talking to the agent, I want it to look at cover art and screenshots in the media
   library, so that it can answer questions about what a download actually contains.
5. As a person talking to the agent, I want text files to behave exactly as they always have, so
   that nothing I already rely on changes.
6. As a person talking to the agent, I want the agent to say when it could not show itself an
   image, so that I can tell an honest failure from a hallucinated description.
7. As a person talking to the agent, I want an image the agent read to stay in view for the rest of
   the exchange about it, so that follow-up questions do not need a fresh read every turn.
8. As a person talking to the agent, I want images the agent reads never to push my own attachments
   out of its view, so that a photo I sent is not forgotten because the agent went looking at files.
9. As a person talking to the agent, I want the agent to know that a picture it is looking at came
   from a file rather than from me, so that it never answers as though I had sent it.
10. As the model, I want one tool for reading a file whatever kind it is, so that I do not have to
    guess which of two tools a path belongs to.
11. As the model, I want the tool's description to say it reads text and images, so that I know the
    capability exists without being told.
12. As the model, I want an envelope naming the path, media type and size of the image I read, so
    that I can quote the path back into another tool call.
13. As the model, I want each image labelled with the virtual path it came from, so that reading
    several images in one batch is unambiguous.
14. As the model, I want every one of my tool calls answered before anything else appears, so that
    the provider does not reject the conversation.
15. As the model, I want to be told when an image was too large to show, along with the size and
    the limit, so that I can resize it myself or tell the person why I cannot look.
16. As the model, I want to be told when the model I am running on cannot accept images at all, so
    that I stop trying and say so.
17. As the model, I want to be told when this host cannot show images, so that I report a
    limitation rather than waiting for a picture that never arrives.
18. As the model, I want a placeholder naming an image that has dropped out of view and inviting me
    to read it again, so that I recover instead of inventing what it contained.
19. As the model, I want `offset` and `limit` to be ignored rather than rejected on an image, so
    that a habit carried over from reading text does not cost me a turn.
20. As the model, I want a clear refusal when a path is neither text nor a viewable image, so that
    I do not receive a zip file rendered as garbage.
21. As the model, I want image reading available on every mount I can already read files from, so
    that I do not have to learn a per-mount rule.
22. As the model, I want to read several images in one batch of parallel calls, so that comparing
    two screenshots costs one turn.
23. As an operator, I want a size ceiling I can set, so that an enormous file cannot turn one turn
    into an enormous request.
24. As an operator, I want the bytes to expire on their own, so that reading images does not grow
    storage without bound.
25. As an operator, I want images the agent read to survive a container restart while they are
    still in view, so that recycling the agent does not blind it mid-conversation.
26. As an operator, I want a host with no state store to keep working, so that a stripped
    deployment degrades to text reading rather than failing.
27. As an operator, I want nothing about my deployed outposts to break, so that machines already
    registered keep working without being rebuilt.
28. As an operator, I want my existing feature configuration to keep working, so that renaming a
    tool does not silently disable file reading for an agent.
29. As an operator, I want context-truncation metrics to keep naming the person who sent the turn,
    so that dashboards do not start reporting the system as the sender.
30. As a maintainer, I want no new filesystem operation, so that the one operation list does not
    grow for something every disk root can already do.
31. As a maintainer, I want the tool renamed in its type as well as its model-facing name, so that
    the code does not contradict what the tool does.
32. As a maintainer, I want one hydration pass covering both attachments and read images, so that
    there is one distance rule and one placeholder shape to maintain.
33. As a maintainer, I want the decision recorded, so that the next person to see a user message
    appear mid-turn does not have to rediscover why the wire made it necessary.

## Implementation Decisions

**The tool is renamed.** `VfsTextReadTool` becomes `VfsFileReadTool` and its model-facing leaf name
becomes `file_read`. The feature key stays `read` and the wire operation stays `fs_read`, so
existing feature configuration keeps selecting it and outpost binaries already deployed keep
advertising it. The capability string follows the leaf name; capabilities are derived hub-side from
wire tool names, so nothing on a remote machine has to change. The description is rewritten to
cover both kinds of file.

**No new filesystem operation.** `FileSystemOperations.All` is untouched. An image path is served
through the raw-byte read every disk root already implements, which is transfer machinery with no
tool key and no capability — an implementation detail of the tool, not a capability the model or a
mount reasons about. The tool is therefore offered exactly where it is today. The three mounts with
no bytes behind them render JSON and markdown and can hold no image file.

**Kind is decided by extension.** `png`, `jpg`, `jpeg`, `gif` and `webp` route to the image path,
aligned with the media types `AttachmentKinds.ForMediaType` already classifies as images. Every
other path takes the text path unchanged. A path that is neither text nor a viewable image gets a
refusal envelope naming why. No magic-byte sniffing.

**The image envelope** carries the virtual path echoed back as the caller spelled it, the media
type, the byte size, and whether the image was shown. It is a distinct result shape from the text
read's; the text result's fields are not repurposed. When `offset` or `limit` were passed on an
image they are ignored and the envelope says so. `shown: false` always carries a note saying which
reason applied: over the ceiling, no image capability on this model, or no store on this host.

**A ceiling bounds one image.** A configured maximum inlined image size, defaulting to 15 MB, read
from the agent host's own settings — a single-host tunable, so not the shared policy file. Over it,
nothing is stored and the envelope names the size and the limit. Images are never downscaled or
re-encoded.

**Model capability is asked per turn.** The existing attachment-capability catalogue answers
whether the model accepts images. A model that does not gets the envelope and no stored bytes.
The check is made when the tool runs, not when the tool set is built, because a turn can override
the model.

**The bytes rest with the agent.** A new `IReadImageStore` contract in the domain layer, written by
the tool and read by the hydration pass, implemented over Redis in the infrastructure layer beside
the other Redis stores. Keyed by conversation and tool call id; the conversation reaches the tool
the way MCP tool metadata already does, and the call id from the function-invocation context. A
time horizon of 24 hours is the backstop. The store is optional in DI exactly as the attachment
source is: a host without one still reads text and answers `shown: false` for images.

**Hydration widens rather than splitting.** It is putting bytes back where a reference sits,
whoever put the reference there. The read-image expansion runs in the same pass over the same
message list, on the way to the model, producing a copy thrown away with the request.

**Placement is after the whole tool-result message.** The function-invoking client builds one
tool-role message holding every result of an iteration, so the expansion inserts exactly one
user-role message immediately after that message and never between its contents. Some providers
reject a conversation in which a tool call is not answered before another message appears.

**The injected message** carries, per image read in that batch, a text label naming the virtual path
followed by the image data. It is attributed to the system as its sender and decorated the way
other turns are, so the model never reads an image it requested as something a person said. There
is no cap on how many images one batch may show.

**Distance.** Read images obey the same message distance as attachments. Injected messages are
excluded from the distance count, as tool calls and results already are, so reading images cannot
age out a person's attachment. On the send where an image drops past the distance its stored bytes
are deleted, making the message window the real bound and the time horizon only a backstop.

**A miss is honest and recoverable.** Out of depth, expired, evicted, or no store at all: the model
gets a placeholder naming the virtual path and telling it to read the file again. Unlike a lost
attachment, the file is still on the mount.

**Sender metrics.** The context-truncation event takes its sender from the last user message.
Injected messages are skipped when picking it, so it keeps naming the person.

## Testing Decisions

Tests are written first, red before green, one triplet per behaviour. A good test here asserts what
the model or the caller can observe — the envelope a tool answers, the message list a chat client
hands onward, the bytes a store returns — and never the shape of an intermediate helper. No test
should name a private type or assert on a call that produced no observable difference.

**The tool seam.** `VfsTextReadToolTests` is renamed with its subject and covers: an image path
answers the image envelope with the echoed virtual path, media type and size; a text path is
unchanged; a non-image binary is refused with a naming envelope; an image over the ceiling answers
`shown: false` with size and limit; a model without image capability answers `shown: false` saying
so; a host with no store answers `shown: false` saying so; `offset` and `limit` on an image are
ignored and noted; bytes are written to the store on success and not written in every refusal case.
Prior art is the existing tool suites in the filesystem tool tests, driving a tool against the
shared backend mocks. `VfsVirtualPathConformanceTests` and `FileSystemToolFeatureTests` pick up the
rename and keep asserting that every tool answers in virtual coordinates and that the feature
exposes the expected keys.

**The chat client seam.** One new suite beside the existing attachment suites, using the same shape:
a mocked inner chat client, capturing the messages the client would send. This is the seam
hydration itself is tested at, which is why the hydration module has no test file of its own, and
no separate suite over the expansion module is added. It covers: an image read in a turn appears as
a user-role message positioned after the whole tool-role message; each image carries a label with
its virtual path before its data; several images in one batch land in one message in order; the
injected message is attributed to the system; the injected message does not shorten the distance at
which a person's attachment is still hydrated; an image past the distance becomes the placeholder
and its stored bytes are deleted; a store miss becomes the placeholder; nothing is injected when
the store holds nothing. Prior art: `OpenRouterChatClientAttachmentTests` and
`OpenRouterChatClientHydrationDepthTests`.

**The store seam.** An integration suite against a real Redis through the existing fixture, like the
other Redis store suites: round trip of raw bytes with media type and path, the time horizon set on
the key, explicit delete, and a miss answering nothing.

**Truncation.** The existing truncation tests cover per-image estimation already; one case is added
only if the injected message would otherwise be uncounted.

## Out of Scope

- Downscaling, cropping or re-encoding an image to fit the ceiling.
- Reporting pixel dimensions, which would mean parsing image headers in the domain layer.
- Magic-byte detection of file kind.
- Any cap on how many images one batch of parallel reads may show. The per-image ceiling is the
  only bound, and a very large batch can still fail as an oversized request.
- PDFs and other documents. The attachment path already treats them as a distinct kind; reading one
  off a mount is a separate decision.
- Writing or generating images.
- Showing images to a subagent by any route other than this tool.
- A new landing or sweeping behaviour: nothing is copied into the sandbox by this feature.
- End-to-end coverage through a real provider.

## Further Notes

The wire constraint is the reason for the whole shape and is easy to re-litigate by accident: a
tool-role message is a plain string on this provider, and content parts are accepted only on user
messages. Returning image data from the tool sends a base64 data URI as tool *text*, which no
provider decodes and which costs roughly 1.4 times the file in tokens. The decision, the evidence
and the rejected alternatives are recorded in ADR 0029.

`CONTEXT.md` already carries the widened definition of hydration and the entry for a read image.
The tool rename still has to reach the virtual filesystem rules file; that edit was deliberately
held back from the documentation pass because that file describes what the code does, and it goes
in with the implementation.
