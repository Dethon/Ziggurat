# 04 — The topic list pages

**What to build:** The sidebar loads one page of topics and fetches the next as the person
scrolls, instead of every topic the space has ever had. A topic that gains a message while
someone is scrolling moves to the top rather than appearing twice or going missing.

Paging fetches backwards only. New activity arrives as a push and inserts at the top, which is
what covers a topic bumped from below the cursor to above it. A bump that happens while the
client is not live is covered by catch-up refetching the first page on becoming live.

**Blocked by:** 03 — The topic index replaces the scan.

**Status:** ready-for-agent

- [ ] The list hub call takes a cursor and a page size and returns a page plus the cursor for
      the next one.
- [ ] Scrolling to the bottom of the sidebar loads the next page.
- [ ] A topic already shown that gains a message moves to the top and is not duplicated.
- [ ] A topic pushed from below the cursor reaches the client.
- [ ] Becoming live after an interruption refetches the first page.
- [ ] Cursor tracking, page appending and deduplication live in a plain class that is tested
      without rendering anything.
- [ ] Scroll-to-load-more is covered end to end in a real browser.
- [ ] The retention policy block exists in the Agent's settings and holds the page size. Nothing
      is added to the compose file or its environment file.
