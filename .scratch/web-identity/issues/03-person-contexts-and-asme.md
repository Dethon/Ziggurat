# 03: Identity map, person contexts and `asMe` (in memory)

**What to build:** `web_browse` gains `asMe`. With it, the tab opens in the turn's person context — the person resolved from the call's conversation context through the identity map — and without it in the anonymous context, which never carries a person's cookies. The identity map is per-deployment values in the browse server's compose environment (placeholders only in the repo), tying a person's Telegram username, WebChat name and enrolled voice speaker together; the voice `household` fallback is no person. An empty map is the feature off. Person contexts are created on first use and live in memory only (remembering arrives in 08). Spec: `../spec.md` ("Identity", "Browser contexts").

**Blocked by:** 02

**Status:** done

- [x] `asMe` from a mapped user opens the tab in that person's context; the tool envelope names `asMe`
- [x] `asMe` from an unmapped user, from `household`, or with an empty map is refused with a message naming the reason; nothing falls back to the anonymous context or another person
- [x] A cookie set in a person's context is absent from the anonymous context and from another person's (real Camoufox)
- [x] Refs stamped in a person-context tab route back to it from `web_action`, `view_image`, `web_snapshot` and `back` without a flag; popups stay in the person context
- [x] Two user ids mapped to one person share one context
- [x] Identity map wired as compose environment entries with placeholders, per the project's per-deployment rule
- [x] Server surface tested with a real MCP client and a fake browser (prior art: the websearch session scope tests)
