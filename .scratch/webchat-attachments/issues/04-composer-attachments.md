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

**Status:** ready-for-agent

- [ ] One file control covers desktop, mobile and the installed app, offering images and PDFs.
- [ ] Pasting an image from the clipboard attaches it.
- [ ] Dropping files onto the composer attaches them.
- [ ] Each file uploads on being picked, with its own progress.
- [ ] A file can be cancelled while uploading and removed before sending.
- [ ] A message with attachments and no text can be sent.
- [ ] Oversized files and unsupported kinds are refused at pick time with a readable reason.
- [ ] The send carries the references for every file that finished uploading.
- [ ] Client tests drive the store's actions against the fake hub connection, covering picking, progress, cancel, remove and text-free send.
