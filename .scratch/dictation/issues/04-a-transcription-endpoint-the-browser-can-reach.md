# 04 — A transcription endpoint the browser can reach

**What to build:** the chat channel server gains a way to turn a recording into words. A
registered user mints a short-lived dictation ticket, posts one audio file with it, and
gets a transcript back. Nothing is stored: no reference is minted, nothing lands in the
upload store, and the sweeper and retention rules never see it.

The dictation ticket sits beside the existing upload and download tickets — same expiry and
pruning — but it is scoped to the space rather than to a topic, and it counts no slot. A
dictation produces composer text, so it must not force a conversation into existence the
way picking a file does, and it must not spend one of a message's attachment slots.

The browser also needs to learn the rules it has to obey, so the existing limits hub call
grows the recording cap and the mis-tap floor. The server enforces the cap itself rather
than trusting the client.

**Blocked by:** 01 — One lemonade transcription client.

**Status:** ready-for-agent

- [ ] A registered user can mint a dictation ticket; an unregistered caller cannot
- [ ] Posting audio with a valid ticket returns the transcript
- [ ] An unknown, expired or wrong-space ticket is refused
- [ ] Audio over the configured cap is refused, whatever the client claims
- [ ] Nothing is written to the upload store, no attachment reference is minted, and no conversation is created by a transcription
- [ ] The existing limits hub call carries the recording cap and the mis-tap floor
- [ ] The reverse proxy routes the new path for every space host, so it does not fall through to the web client
- [ ] Tested over the test server with a fake transcriber, in the style of the existing attachment endpoint tests
