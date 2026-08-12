# 08 — History loads when a topic is opened

**What to build:** Opening the chat client no longer loads the full message history of every
topic in the space. History is fetched when a topic is opened, and the whole conversation is
loaded then so scrolling back through it needs no further waiting.

This is where 06 and 07 pay off: previews and badges keep working because the server now
supplies them.

**Blocked by:** 06 — Row previews come from a stored snippet; 07 — Unread comes from a message
count and a read position.

**Status:** ready-for-agent

- [ ] Starting the chat client loads no message history.
- [ ] Switching agent loads no message history.
- [ ] Becoming live after an interruption loads no message history beyond the open topic.
- [ ] Opening a topic loads all of its history.
- [ ] Badges and previews are correct for topics that have never been opened.
- [ ] Tests that assert every topic's history reaches the client store are rewritten to assert
      that it does not.
