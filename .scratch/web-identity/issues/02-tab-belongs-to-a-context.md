# 02: Prefactor — a tab belongs to a browser context

**What to build:** The tab protocol stops assuming one browser context for the process. A browse names the context its tab opens in, a tab remembers its context for life, reuse-before-open matches only tabs in the same context, and a popup joins its opener's context. Only the existing anonymous context exists after this ticket, so nothing changes for the model or the user — this makes tickets 03 onward an easy change. Spec: `../spec.md` ("Browser contexts").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] Every existing tab protocol and browser integration test passes unchanged
- [x] A tab protocol test shows a URL open in one context is not reused for a browse addressed to another
- [x] A tab protocol test shows a popup is adopted into its opener's context
- [x] The browser's reconnect and stale-connection teardown handle more than one context
