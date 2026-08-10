# WebChat attachments

Status: ready-for-agent
Date: 2026-08-09

Vocabulary is pinned in `CONTEXT.md` under "Chat attachments": **attachment**, **attachment
reference**, **upload store**, **hydration**, **attachment capability**. Two decisions have
ADRs and are not reopened here: `0020` (an attachment is stored once and hydrated on the way
out) and `0021` (the upload store is not a mount).

## Problem Statement

A person using WebChat can only send text. When what they want to ask about is a photo, a
screenshot or a PDF, they have to describe it in words, or give up and use something else.
Meanwhile the agent they are talking to may be running on a model that reads images perfectly
well, and where the agent has a sandbox it could run code against a file it was given — none of
which is reachable, because there is no way to hand it one.

Not every model can take a file. Of the three models offered in the model dropdown today, only
one accepts images and documents; the other two are text only. Whatever the feature does, it
has to make that difference visible before someone spends time attaching a file, and it has to
do so without a person maintaining a hand-written list of which model can do what.

## Solution

The composer gains attachments. Files are chosen with the platform's own picker, pasted from
the clipboard, or dropped onto the composer, and they upload while the person carries on
typing. When the message is sent, the attachments go with it: the model sees them, and where
the agent has a sandbox the files are also written there so tools can work on them.

Whether a model can take an attachment is discovered rather than configured. The agent asks the
model provider what each model accepts and publishes it with the rest of its catalogue, so the
composer knows before anything is sent. Attaching a file to a text-only model stops at the
send button with an explanation naming the model, and the files stay attached so the person can
switch model instead of starting over. A guard on the server refuses the same case, for the
race where the model changes between picking and sending.

Attachments stay visible in the transcript: thumbnails for images, a named chip for other
files, both downloadable. They stay visible to the model too, for a while — long enough that
follow-up questions about a photo work — after which the model sees a placeholder naming the
file while the transcript still shows the real thing.

## User Stories

1. As a chat user, I want to attach an image to a message, so that I can ask about something I
   cannot easily describe in words.
2. As a chat user, I want to attach a PDF, so that I can ask questions about a document without
   copying its text into the composer.
3. As a chat user, I want to attach several files to one message, so that I can ask about a set
   of things together rather than one message at a time.
4. As a desktop user, I want to paste a screenshot straight into the composer, so that I do not
   have to save it to disk first.
5. As a desktop user, I want to drag files onto the composer, so that attaching matches how
   every other application I use behaves.
6. As a mobile user, I want the platform's own sheet with Photo Library, Take Photo and Choose
   File, so that attaching works the way it does in every other app on my phone.
7. As someone using the installed PWA, I want attaching to work exactly as it does in the
   browser tab, so that installing the app costs me nothing.
8. As a chat user, I want to send a photo with no caption at all, so that I am not made to type
   something meaningless first.
9. As a chat user, I want to see each file uploading with its own progress, so that I know
   whether a large file is moving.
10. As a chat user, I want to remove a file I attached by mistake before sending, so that I do
    not have to abandon the message.
11. As a chat user, I want to cancel an upload in progress, so that a file I picked by accident
    does not hold up my message.
12. As a chat user, I want to keep typing while files upload, so that waiting on the network
    does not block me.
13. As a chat user, I want to be told before sending when the selected model cannot read what I
    attached, so that I do not waste a turn discovering it.
14. As a chat user, I want my attached files to survive switching model, so that the fix for a
    capability refusal is picking a different model rather than starting again.
15. As a chat user, I want to be told which model is refusing and why, so that I know which one
    to switch to.
16. As a chat user, I want a file that is too large or of an unsupported kind to be refused as
    I pick it, so that I find out immediately rather than after an upload.
17. As a chat user, I want to see my attachments in the transcript after sending, so that the
    conversation records what I actually sent.
18. As a chat user, I want images to appear as thumbnails in the transcript, so that I can tell
    at a glance which photo a message was about.
19. As a chat user, I want non-image files shown as a named chip, so that I can see what was
    sent without a meaningless preview.
20. As a chat user, I want to download a file I sent earlier, so that the conversation is a
    place I can get things back from.
21. As a chat user, I want attachments to still be there after I reload the page, so that the
    transcript is a record and not a session.
22. As a chat user, I want attachments to appear on my other device watching the same topic, so
    that the conversation looks the same everywhere I have it open.
23. As a chat user, I want to ask a follow-up question about an image I sent a few messages
    ago, so that I do not have to re-attach it to continue the same line of thought.
24. As a chat user, I want an attachment that is too old for the model to still show in my
    transcript, so that scrolling back shows me what happened rather than a hole.
25. As a chat user, I want the model told plainly when a file is no longer available to it, so
    that it says so instead of inventing what the file contained.
26. As a chat user talking to an agent with a sandbox, I want the file to exist as a real file
    there, so that the agent can run something against it rather than only look at it.
27. As a chat user talking to an agent with a sandbox, I want the agent to know the file's path
    without me asking, so that "have a look at this" is enough.
28. As a chat user talking to an agent with no sandbox, I want attachments to still work as
    context, so that the feature does not silently depend on which agent I picked.
29. As a chat user, I want two files with the same name in one conversation to both survive, so
    that sending `scan.pdf` twice does not lose the first one.
30. As a chat user, I want a failure to put the file in the sandbox not to lose my turn, so
    that a broken tool does not cost me an answer the model could still give.
31. As a chat user, I want deleting a topic to delete the files I sent in it, so that removing
    a conversation removes what was in it.
32. As someone sharing a space, I want attachments scoped to my space, so that a link to a file
    is not readable by people in another one.
33. As an operator, I want the maximum file size and count to be settings, so that I can tune
    them for my deployment without a code change.
34. As an operator, I want how far back attachments stay visible to the model to be a setting,
    so that I can trade token cost against how long follow-up questions keep working.
35. As an operator, I want uploaded files swept after a retention period, so that abandoned
    uploads and undeleted topics do not fill the disk.
36. As an operator, I want the model capability list refreshed while the system runs, so that a
    model gaining image support is picked up without a restart.
37. As an operator, I want a failed capability lookup not to disable attachments, so that a
    blip at the provider does not remove a feature from everyone.
38. As an operator, I want the upload endpoint to refuse callers without a valid ticket, so
    that a public hostname does not mean public disk.
39. As a developer, I want the attachment shape on the channel message to be transport-neutral,
    so that adding it to Telegram later is new channel code and nothing else.
40. As a developer, I want reading a conversation's history to cost the same whether or not
    files were sent, so that attachments do not slow down every turn of every conversation.
41. As a developer, I want the token estimate to account for attachments, so that a large
    document does not silently overflow the context window.

## Implementation Decisions

**Attachment references cross every boundary; bytes cross as few as possible.** The channel
message gains a list of attachment references — where the file rests, media type, filename,
size. That list is what the hub call carries, what the channel notification carries, and what a
conversation's persisted history keeps. Bytes travel from the browser to the upload store, and
from the upload store to the agent, and nowhere else.

**The upload store lives on the SignalR channel server.** It accepts files, mints references,
serves them back, and sweeps. It is not a mount and is never mounted (ADR `0021`); the agent
reaches it through a channel-protocol tool hidden from the model, the same way it reaches every
other channel-server capability.

**Uploads happen at pick time, over HTTP, one request per file.** The client asks the hub for
an upload ticket scoped to the topic with a short TTL, then posts each file. One request per
file is a requirement, not a style choice: the web host's default request body cap is below the
combined size of ten files at the configured maximum. The hub's own message size limit is left
alone, because bytes never ride the hub.

**Limits are 25 MB per file and 10 files per message, images and PDFs only, originals as
picked.** No browser-side downscaling: providers resize server-side into their own tile scheme
before billing, so it would save bandwidth and disk while costing the sandbox the true file.

**Attachment capability is discovered from the model provider, cached, and published with the
agent catalogue.** The agent fetches the provider's model list at startup and hourly, keeps the
accepted input kinds per model, and includes them in the catalogue it registers with each
channel — for its default model and for every patchable model. A failed fetch falls back to the
last known good values; with nothing cached, attachments are allowed and the failure surfaces
later as a refusal. Failing open is deliberate: a transient provider problem must not remove
the feature.

**Capability is refused twice, at different costs.** The composer blocks the send and explains,
keeping the files attached. The channel server refuses before emitting anything, answering on
the same stream-error path an undeliverable message already uses, so no turn is created and no
agent is woken. The agent itself does not re-check; there is no third guard.

**The effective model is the per-message patch, falling back to the agent default.** Both the
composer and the channel server resolve it the same way, from the same catalogue.

**Turn build attaches contents and, where possible, writes a file.** When the conversation
group builds the user message it fetches the bytes for each reference and adds them as content
alongside the text. Where the agent has a sandbox, each file is also written into the sandbox
under a per-conversation, per-message directory, keeping the user's own filename; the resulting
virtual path is named in the message so the model can act on it without being told a tool
exists. A per-message directory is what removes collision handling entirely. A failed sandbox
write is logged and the turn proceeds as context-only.

**Hydration replaces references with bytes on the way out and never on the way in**, reaching
back a configured number of messages, defaulting to 20, with one rule for every attachment
kind. Beyond that distance, and for any reference whose file is gone, hydration produces a
placeholder naming the file. This sits at the same point in the pipeline as existing user-turn
decoration. Full reasoning in ADR `0020`.

**The token estimator gains an attachment case.** Without one it counts a large document as a
fixed handful of tokens and truncation goes blind.

**History projection keeps attachments and stops discarding text-free messages.** The channel
server's history read currently keeps only text content and drops messages whose text is empty,
which would make an image-only message vanish on reload. It must project attachment references
alongside the text and keep messages that have one.

**Downloads are minted, not published.** The client asks the hub for a short-lived download URL
when the transcript renders an attachment, and the request is checked against the requester's
space, because one upload store serves every space. No long-lived URLs.

**Retention has two mechanisms.** Deleting a topic removes that conversation's files. A sweep
collects everything topic deletion never reaches — undeleted topics, and uploads for messages
that were abandoned before sending — on a window matching the existing history retention.
Sandbox copies belong to the agent and are not swept.

**Settings live in the application settings file**: maximum bytes, maximum files, hydration
depth in messages, retention days, ticket TTL. The upload store's path is a per-deployment
volume and belongs to the compose file. The reverse proxy needs routes for the upload and
download endpoints; it currently forwards only the hub and the agent API paths to this server.

## Testing Decisions

A good test here pins behaviour someone could describe without knowing how it was built: what
the composer refuses, what the channel server emits, what the model receives, what comes back
after a reload. Tests assert on those observable results, not on which method called which. TDD
per the repo rule: a failing test first, watched failing, then the implementation.

Six seams, all with existing precedent, and no new ones introduced beyond one endpoint fixture.

**Client store.** Drive the store's actions against the fake hub connection, as the not-live
and topic-stream suites do. Covers: files being picked and their upload state, removal and
cancellation, the send being blocked for an incapable model and the files surviving it, sending
with no text, and what the transcript holds after a history load. Prior art:
`NotLiveUserActionTests`, `TopicStreamFlowTests`.

**ChatHub.** Drive the hub directly with real services, as the cancel and not-live hub suites
do. Covers: ticket minting and scope, refusal before emit and the shape of the error, the
attachment list reaching the notification, download URL minting and the space check, and topic
deletion removing files. Prior art: `ChatHubNotLiveTests`, `ChatHubCancelTopicTests`.

**Upload and download endpoints.** A test-server host as the voice channel's endpoint suites
use, with real multipart bodies. Covers: refusal without a ticket, with an expired ticket and
with a ticket for another topic; refusal of an oversized file and a disallowed type; a
successful upload minting a usable reference; download returning the bytes. Prior art:
`AnnounceEndpointAuthTests`, `SatellitesEndpointTests`.

**ChatMonitor.** Drive the monitor with fake channels and assert what the agent received, as
the config-patch and conversation-context suites do. Covers: references becoming message
contents, the sandbox path appearing in the message, a sandbox-less agent still getting
context, and a failed sandbox write leaving the turn intact. Prior art:
`ChatMonitorConfigPatchTests`, `ChatMonitorConversationContextTests`.

**Chat client pipeline.** Construct the OpenRouter chat client over a fake inner client and
assert on the messages that reach it. Covers: hydration within the depth, placeholder beyond
it, placeholder for a missing file, references never being written back into history, and the
token estimate accounting for attachments. Prior art: the existing reasoning and latency suites
around the same client.

**Capability lookup.** WireMock for the provider's model list, already referenced by the test
project. Covers: parsing accepted kinds, the hourly refresh, last-known-good on failure, and
failing open with nothing cached.

**One E2E.** Playwright against the compose stack, using the file-input API: attach an image,
send, see it in the transcript, reload, see it still. Tagged as E2E and skippable like its
neighbours in the WebChat E2E suite.

## Out of Scope

Telegram attachments. The channel message shape is deliberately transport-neutral so that work
is new channel code and no redesign, but it is not this spec.

Audio and video attachments. Document types other than PDF — no conversion layer for
office formats, no text extraction for a text-only model. Attachments produced by the agent in
its replies. Browser or server-side image downscaling. Parsing a PDF to text so a text-only
model can read it: if that is ever wanted it is a new decision, not an extension of this one.
Mounting the upload store, now or later, for the reasons in ADR `0021`. Any per-conversation
isolation inside the sandbox beyond directory layout — the sandbox is already shared by every
conversation of every agent configured with it, and this feature does not change that.

## Further Notes

Two of the three models currently offered in the dropdown are text-only, so the refusal path is
the common case rather than the corner. Build and test it as a first-class flow.

PDFs go only to models advertising file input, which for those models means native handling
rather than a parsing plugin, so there is no per-page parsing charge on this path. That
property is a consequence of the capability check, not an independent guarantee — if the check
is ever loosened, the charge appears.

Retention is measured in days and hydration in messages, so they cannot be reconciled exactly.
A busy conversation moves past the hydration depth long before the sweep runs, a quiet one does
not. The placeholder path is therefore ordinary behaviour, not an edge case, and should be
visible in the transcript as a named file rather than a gap.
