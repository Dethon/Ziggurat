# 04 — Composer attachments

**What to build:** A person can attach files to a message and send them. Files are chosen with
the platform's own picker — one control, so the phone offers photo library, camera and file
browsing, and the installed app behaves exactly like the browser tab — or pasted from the
clipboard, or dropped onto the composer. Each file uploads as soon as it is picked, showing its
own progress, and can be cancelled while it uploads or removed before sending. Typing carries
on throughout.

Sending with attachments and no text at all is allowed. A file that is too large or of an
unsupported kind is refused as it is picked, not after it uploads.

This is the first slice a person can use: attach a photo, send, get an answer about it.

**Blocked by:** 01 — Upload store and its endpoints; 03 — An attachment reaches the model.

**Status:** resolved

- [x] One file control covers desktop, mobile and the installed app, offering images and PDFs.
- [x] Pasting an image from the clipboard attaches it.
- [x] Dropping files onto the composer attaches them.
- [x] Each file uploads on being picked, with its own progress.
- [x] A file can be cancelled while uploading and removed before sending.
- [x] A message with attachments and no text can be sent.
- [x] Oversized files and unsupported kinds are refused at pick time with a readable reason.
- [x] The send carries the references for every file that finished uploading.
- [x] Client tests drive the store's actions against the fake hub connection, covering picking, progress, cancel, remove and text-free send.

## Comments

A ticket is scoped to a topic, so attaching needs one. Picking a file into a composer with no
conversation selected now starts the conversation first, named after the file, and then uploads —
the same session start the send path does, one step earlier. Without it the first thing a person
could do in a fresh WebChat would be refused.
