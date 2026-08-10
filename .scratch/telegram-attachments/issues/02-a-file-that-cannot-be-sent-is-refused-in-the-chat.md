# 02 — A file that cannot be sent is refused in the chat

**What to build:** A person hears about a file that could not be sent, in the chat, while they are
still there — rather than getting silence or an answer written as if the file had arrived.

Two grounds, both properties of one file. A file larger than Telegram's 20 MB download limit is
refused on arrival: the size is on the update before anything is downloaded, so nothing is fetched
to discover it. A file whose kind resolves to neither image nor document is refused the same way.

Both are per-file. The offending file is dropped and the turn still runs on the caption and every
other file that came through, so one bad file in five does not make someone resend the other four.
All refusals for one message are reported in a single reply that quotes the message that failed,
matching how the existing unauthorised-user reply works, and naming the files it is about.

This ticket establishes the refusal reply that 04 reuses and 05 suppresses.

**Blocked by:** 01 — A photo or a document reaches the model.

**Status:** resolved

- [x] A file above the download limit is refused without any download being attempted.
- [x] A file whose kind resolves to nothing is refused.
- [x] A refused file is dropped and the turn still runs, carrying the caption and the remaining attachments.
- [x] A message whose every file is refused still runs as a text turn when it has a caption, and runs no turn at all when it has none — the reply is the whole response.
- [x] All refusals for one message produce a single reply quoting the message that failed and naming the files.
- [x] Tests cover each ground, the mixed case where some files survive, and the reply's content and quoting.
