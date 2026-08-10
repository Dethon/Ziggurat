# Telegram attachments

Status: ready-for-agent
Date: 2026-08-10

Vocabulary is pinned in `CONTEXT.md` under "Chat attachments": **attachment**, **attachment
reference**, **upload store**, **hydration**, **attachment capability**. Three decisions have ADRs
and are not reopened here: `0020` (an attachment is stored once and hydrated on the way out),
`0021` (the upload store is not a mount) and `0022` (a channel whose transport keeps files has no
upload store).

## Problem Statement

A person talking to an agent through Telegram can only send text. Telegram is the channel most
used away from a desk, and it is exactly where a photo is the natural thing to send: a screenshot
of an error, a picture of a broken part, a PDF that arrived in a mail. Today the bot ignores all
of it. A media message does not even reach the agent — the poll loop drops everything that is not
a text message — so a person who sends a photo gets silence and no explanation.

The machinery to do better already exists and is transport-neutral. WebChat gained attachments,
and everything above the channel was built to be shared: the notification and the channel message
both carry attachment references, the history keeps references and never bytes, hydration puts the
bytes back on the way to the model, and where an agent has a sandbox the files land there as real
files. Only the upload store and the fetch tool were SignalR's own. Telegram needs its own answer
to where the bytes rest, and nothing else.

Not every model can read an image, and Telegram has no composer to warn anyone before they send.
Whatever the feature does, the person has to hear about a refusal in the chat rather than have
their turn fail somewhere they cannot see.

## Solution

Sending a photo or a PDF to the bot works. The caption is the message, the files ride with it, and
the model sees both. Several photos sent as one album become one turn carrying all of them, with
the album's caption as the question, rather than one turn per photo. A message with files and no
caption at all is a legitimate turn.

Telegram already keeps the file, so nothing is copied anywhere. The attachment reference names the
bot and Telegram's own handle for the file, and when the agent asks for the bytes the channel
fetches them from Telegram at that moment. There is no store, no volume, no retention setting and
nothing to sweep on this channel, and a reference stays fetchable for as long as the bot exists.

Refusals happen in the chat, while the person is still there, quoting the message that failed. A
file over Telegram's 20 MB download limit is refused on arrival, because its size is known before
anything is downloaded. So is a file that is neither an image nor a PDF. Both are per-file: the
turn still runs on the caption and whatever else came through, so one bad file in five does not
make someone resend the other four. A model that cannot read what was attached is different — that
refusal stops the turn, because an answer that silently ignores what was sent is worse than no
answer.

Whether the model can read an attachment is discovered, not configured. The agent already
registers its catalogue with every channel it connects to; Telegram starts accepting that
registration and asks the same capability resolution WebChat asks, so the two cannot disagree.

## User Stories

1. As a Telegram user, I want to send a photo with a caption asking about it, so that I can ask about something I can see instead of describing it in words.
2. As a Telegram user, I want to send a PDF and ask what is in it, so that I do not have to copy text out of it by hand.
3. As a Telegram user, I want to send several photos as one album, so that the agent answers about all of them together rather than once per photo.
4. As a Telegram user, I want my album's caption to be the question for the whole album, so that it is not attached to an arbitrary one of the photos.
5. As a Telegram user, I want a straggling photo in a slow upload to still join its album, so that the turn does not start with files missing.
6. As a Telegram user, I want to send files with no caption at all, so that I can just show the agent something and let it respond.
7. As a Telegram user, I want an image sent as a file rather than a photo to work the same way, so that I can keep the original quality.
8. As a Telegram user, I want a document whose type Telegram describes vaguely to still be recognised by its extension, so that a PDF is not refused over a technicality.
9. As a Telegram user, I want to be told when a file was too large, so that I can compress it or send it another way instead of waiting for an answer that never comes.
10. As a Telegram user, I want to be told when a file type is not supported, so that I know the agent never saw it.
11. As a Telegram user, I want to be told when the model I am talking to cannot read images, and which model that is, so that I know what to switch to.
12. As a Telegram user, I want a refusal to quote the message it is about, so that I know which of my five photos failed.
13. As a Telegram user, I want the rest of my message to go through when only one file was refused, so that I do not have to resend everything.
14. As a Telegram user, I want a refusal about the model to stop the turn entirely, so that I never get an answer written as if I had sent nothing.
15. As a Telegram user, I want a reaction sticker to not draw a complaint about unsupported file types, so that the chat stays quiet.
16. As a Telegram user, I want a sticker I send deliberately to reach a model that can read images, so that I can ask about one.
17. As a Telegram user, I want to ask a follow-up question about a photo I sent a few messages ago, so that a conversation about an image works like any other conversation.
18. As a Telegram user, I want to be told plainly when the agent could not get hold of a file, rather than being given an invented description of it.
19. As a Telegram user talking to an agent that has a sandbox, I want my file to exist there as a real file with a sensible name, so that the agent can run something against it.
20. As a Telegram user, I want the file I sent last week to still work after a deployment, so that a restart does not lose what I sent.
21. As a Telegram user in a plain chat, I want the existing addressing rule to be unchanged, so that the bot does not start answering every photo posted in a group.
22. As an operator, I want no new volume or storage path on the Telegram channel, so that deploying it stays exactly as simple as it is today.
23. As an operator, I want no new settings to tune, so that the channel follows Telegram's own limits rather than a number I have to keep correct.
24. As an operator, I want disk use on the Telegram channel not to grow with attachments, so that there is no retention sweep to reason about there.
25. As a maintainer, I want the Telegram channel to populate the same attachment fields WebChat does, so that nothing above the channel knows which transport a file came from.
26. As a maintainer, I want the capability refusal to use the same resolution WebChat uses, so that two channels cannot disagree about which model is refusing.
27. As a maintainer, I want the agent to publish its catalogue to Telegram through the registration it already performs, so that no new protocol call is added.
28. As a maintainer, I want the refusal rules to be testable without a clock, so that adding a rule later is not a fight with a timer.
29. As a maintainer, I want an attachment reference to name the bot that can fetch it, so that a fetch works from a cold start with no chat mapping to consult.
30. As an agent, I want a fetch that fails to cost the turn only its picture, so that an unreachable file never costs an answer.

## Implementation Decisions

**Scope is inbound only.** A person's files reach the model. The agent still replies in text; there
is no way for it to send a file into the chat, and no new reply content type.

**Telegram is the store (ADR 0022).** Nothing is downloaded on receipt. An attachment reference's
id is `<agentId>/<Telegram file id>`, and the fetch resolves the bot from the first segment and
does a get-file plus a download at the moment the agent asks. No storage path, no volume, no
sweeper, no retention, and no attachment settings on this channel. The reference is durable for as
long as the bot exists.

**A sixth channel-protocol tool on Telegram.** The channel gains a fetch-attachment tool with the
same name and shape SignalR's has, hidden from the model like every other channel-protocol tool.
An empty answer means the bytes could not be had, which hydration turns into a placeholder.

**Telegram accepts agent registration.** The channel gains a register-agents tool, a three-line
copy of the voice channel's, backed by the mutable agent catalogue that already lives in the domain
layer. It exists solely so the capability resolution has a catalogue to ask; nothing else consumes
it, and no new command is added. The resolution is permissive while the catalogue is silent, so a
cold start does not remove the feature.

**Addressing is unchanged, with the caption standing in for the text.** A media message qualifies
under exactly the rule text does today: a caption beginning with the command prefix, or a message
in a forum thread. A message with attachments and an empty caption is a valid turn and its content
is empty.

**Two new collaborators in the channel, both internal.** An intake turns a Telegram message into
attachment references or refusal reasons and holds no clock, so every rule below is a pure
function. An album buffer owns the debounce and is the only thing that needs time. The polling
service stays a pump and gains a time provider so the buffer's clock is injectable.

**Albums become one turn.** Messages sharing a media-group id are held and released as a single
notification carrying every reference and the group's caption. The window is a sliding 1.5 second
debounce reset by each arrival, with no ceiling and no early release when the group reaches
Telegram's limit: a slow upload must not split a group, and the window only ever extends when
another item of the same group actually arrives. The debounce is a constant, not a setting. Held
updates are already acknowledged to Telegram, so a crash inside the window loses the group; the
exposure is bounded by the upload and accepted.

**Kind is decided by mime type first, extension second.** The existing media-type mapping is
reused unchanged: image types are images, PDF is a document, everything else resolves to nothing.
When Telegram gives no mime type or a generic one, the filename extension decides. A photo sent as
a file is therefore an image, which the existing mapping already handles.

**Static stickers are images.** They carry an image mime type and go through as attachments like
any other image, including the capability stop when the model cannot read them.

**Naming.** A document keeps the filename Telegram carried. Media with no filename — photos,
stickers — is named `attachment-<message id>.<extension from mime>`, falling back to a binary
extension. The extension is load-bearing once the file lands in a sandbox, and the message id
makes two unnamed items of one album distinct without relying on the reference id, which is long.

**Three refusal grounds, two kinds.** Over 20 MB, and unresolvable kind, are per-file: the file is
dropped and the turn runs on the caption and the survivors. A capability refusal is per-turn: the
turn does not run at all. Sizes are read off the update, so the large-file refusal happens before
anything is downloaded. All refusals for one message are reported in a single reply that quotes
the message that failed, matching how the existing unauthorised-user reply works.

**Expressive media that resolves to no kind is dropped silently.** An animated or video sticker, an
animation, a video note: the person chose an expression rather than a file, so no reply. An
unresolvable document or video does get the reply, because attaching one was deliberate.

**Nothing above the channel changes behaviour.** The notification's attachment list, the channel
message, the conversation group's reference stamping, hydration, sandbox landing and the agent's
attachment source all work unmodified. Comments in the domain layer stating that only the SignalR
channel populates attachments stop being true and are corrected.

**No cache.** The agent's existing bounded cache of fetched bytes is the only one. A bounded
in-memory cache in the Telegram channel is the named escape hatch if fetching proves slow; it is
additive and changes no contract, so it is not built before there is a measurement.

## Testing Decisions

A good test here drives real Telegram updates in and asserts on what a person or the agent would
observe: the notification that reaches the channel inbox, and the messages the bot sends back.
Nothing asserts on the intake or the album buffer directly — they exist to keep the rules free of a
clock, not to be a public surface — and nothing asserts on how a reference was assembled beyond
what a fetch then does with it.

Two seams, both already in use, and no new ones.

**The pump.** The existing polling-service test fixture drives updates through the real service
against a mocked bot client, with a real channel inbox and emitter and a fake time provider. Prior
art is the whole of the existing polling-service test file, including its authorisation and
non-text cases. Behaviours covered here: caption addressing in a plain chat and in a forum thread;
an empty caption producing a turn with no text; album batching, the sliding debounce, and a
straggler joining its group; the reference shape and both naming rules; mime-first kind resolution
with extension fallback; each refusal ground and the per-file versus per-turn asymmetry; the reply
quoting the failing message; static stickers going through and expressive media being dropped in
silence. The existing test asserting that a photo message is ignored inverts.

**The tool entry points.** Prior art is the existing send-reply and request-approval tool tests,
which call the tool method directly with a hand-built service provider. The fetch tool is fully
exercisable through the mocked bot client: the get-file request is answered by the same mechanism
the existing tests use, and the download method is an interface member, so a callback supplies the
bytes. Covered: bytes returned for a known reference, an empty answer for an unknown one, and the
bot being resolved from the reference's first segment with no prior chat mapping. The
register-agents tool is covered by asserting the catalogue it replaces.

Capability refusal crosses both seams — register a catalogue through the tool, then drive a photo
through the pump — and that is the truest form of the test rather than a gap.

Unit tests only. Nothing here is a question about Telegram's wire protocol that a mocked client
would answer wrongly, and a test driving a real bot token would cover Telegram's servers rather
than this code. Red first, per the repo's TDD rule.

## Out of Scope

- **Outbound files.** The agent cannot send a photo or a document into the chat. That needs a new
  reply content type and touches every channel's send path.
- **Voice, audio and video as attachments.** They would need new attachment kinds and a capability
  question the provider catalogue may not answer, and voice already has its own channel.
- **Any byte cache on the Telegram channel**, in memory or on disk. Named as an escape hatch in
  ADR 0022 and deliberately unbuilt.
- **Attachment settings for this channel.** The size ceiling, the album limit and the accepted
  kinds are facts about Telegram and about the shared kind mapping, not operator knobs.
- **Any change to WebChat, the voice channel or the service bus channel.**
- **Raising the 20 MB ceiling.** It is Telegram's bot API limit and lifting it means running a
  local bot API server.

## Further Notes

The download ceiling is Telegram's, quoted from its bot FAQ: "Use the getFile method. Please note
that this will only work with files of up to 20 MB in size." SignalR allows 25 MB, so the two
channels differ on what fits, and that asymmetry is a property of the transports rather than
something to reconcile.

A Telegram file handle is per-bot, which is why the reference names the agent. This also scopes a
reference correctly for free: a handle obtained by one agent's bot cannot be fetched by another's.

Availability moves from receipt time to hydration time. If Telegram is unreachable when a turn
runs, an attachment hydrates to a placeholder even though nothing was ever lost. In exchange
nothing expires, so the placeholder path that retention makes routine on WebChat is here only ever
a transient failure.

The glossary entry for the upload store was widened alongside ADR 0022: it now describes one
channel's answer to where bytes rest rather than something every channel has.
