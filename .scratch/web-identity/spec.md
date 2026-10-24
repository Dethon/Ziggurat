# Browsing as a person: handoff logins

Status: ready-for-agent

## Problem Statement

The agent can read the open web but nothing behind a login. When I ask it to check an order, look
at a booking, read a statement or do something in an account of mine, it stops at the sign-in page.
There is no way to give it my session, and handing it my password is not something I want to do:
the model would see the secret, and it would still be stuck at every 2FA prompt and captcha.

The browser underneath makes it worse than missing: there is one browser context for the whole
deployment, so a login made by any turn would be shared by every person and every conversation,
and it would vanish the next time Camoufox restarts. Two people live with this agent, and one of
them must never be able to act as the other.

## Solution

I log in myself and the agent inherits the session. When a turn needs an account of mine, the
agent opens the site's login page in my own browser context and sends me a one-time link. The link
opens a live view of that page: I see it, click and type into it — password, 2FA, captcha, a Google
sign-in redirect — and press Done. The agent is woken in the same conversation and carries on with
what I asked, logged in as me, without me typing anything else. The model never sees what I typed.

The login is remembered by default (a box on the live view, ticked), so later conversations and
scheduled fires are logged in too, across restarts, until I forget the site or the site logs me
out. Unticked, the login lives only until the browser restarts. A page reachable by a one-time link
lists the sites I am logged into and lets me forget any of them.

The agent only uses my session when it asks for it on a browse. Ordinary browsing never carries my
cookies, so a page found through a search cannot make requests to my accounts. Every person has
their own context; my turns reach my logins and nobody else's.

On voice, nabu cannot speak a link, so the link is put in a WebChat conversation of mine and nabu
says so; when I finish, nabu continues on the satellite.

## User Stories

1. As a person, I want the agent to read pages of an account of mine (orders, bookings, statements), so that I can ask about them like any other page.
2. As a person, I want the agent to act in an account of mine (buy, book, send, change settings) without asking me first, so that a delegated task finishes without me in the loop.
3. As a person, I want to log in myself rather than give the agent my password, so that no secret of mine ever passes through the model.
4. As a person, I want to complete 2FA, captchas and single-sign-on redirects myself during the login, so that sites with strong sign-in still work.
5. As a person, I want the agent to send me a one-time link when it needs a login, so that I can do the handoff from my phone wherever I am talking to it.
6. As a person on Telegram, I want the link in the agent's reply, so that I tap it right there.
7. As a person on WebChat, I want the link in the agent's reply, so that I open it in another tab and come back.
8. As a person talking to nabu, I want the link placed in a WebChat conversation of mine and nabu to tell me so, so that a voice request can still reach a login.
9. As a person, I want a Web Push ping when the voice handoff link lands in WebChat, so that I notice it without opening WebChat first.
10. As a person, I want the live view to show the page as it really is and take my clicks, typing and scrolling, so that logging in feels like using a browser.
11. As a person, I want to navigate freely inside the live view, so that an SSO redirect to another site or a "forgot password" detour works.
12. As a person, I want a Done button on the live view, so that I decide when the login is finished.
13. As a person, I want pressing Done to wake the agent in the same conversation and continue my task, so that I don't have to type "done" or repeat my request.
14. As a person on voice, I want nabu to speak the result on the satellite I asked from after I press Done, so that the voice request completes where it began.
15. As a person on voice who has walked away, I want the result still spoken on that satellite as an announcement, so that the answer is not lost.
16. As a person, I want "Remember this login" ticked by default, so that one handoff covers later conversations and restarts.
17. As a person, I want to untick Remember for a sensitive site, so that it is never written to storage and is gone once the browser restarts.
18. As a person, I want a remembered login to stay until I forget it or the site logs me out, so that I don't re-login on an arbitrary schedule.
19. As a person, I want the session re-saved as it is used, so that cookies the site rotates are not lost and the session does not go stale early.
20. As a person, I want the agent to notice it has been logged out and start a new handoff, so that an expired session is a small detour, not a failure.
21. As a person, I want a scheduled fire that finds itself logged out to say so in its report, so that I know why a morning check failed.
22. As a person, I want scheduled fires to use my remembered logins, so that "check my order every morning" works.
23. As a person, I want workers spawned by my agent to use my logins on the tabs they share with the parent, so that delegated research behind a login works.
24. As a person, I want a worker that hits a login wall to report it to its parent instead of minting a link, so that a handoff only ever comes from a conversation I'm in.
25. As a person, I want my logins reachable only from my own turns, so that the other person in the household cannot act as me.
26. As a person, I want my Telegram username, my WebChat name and my enrolled voice to resolve to the same me, so that one handoff covers every channel.
27. As a person who isn't in the identity map, I want a browse asking for my session to be refused plainly, so that nothing falls back to someone else's identity or to the shared context.
28. As a person, I want the agent's ordinary browsing to never carry my cookies, so that a malicious page from a search cannot reach my accounts.
29. As a person, I want the transcript to show when a browse used my session, so that I can see what was done as me.
30. As a person, I want a handoff link to work once and for ten minutes, so that a leaked or forwarded link is useless.
31. As a person, I want an expired or abandoned link to do nothing, so that walking away from a login is harmless.
32. As a person, I want to ask again and get a fresh link, so that missing the first one costs nothing.
33. As a person, I want a page listing the sites I'm logged into, remembered and not, so that I know what the agent can reach.
34. As a person, I want a Forget button per site on that page, so that I can revoke access at any time.
35. As a person, I want the list page reached by a one-time link minted for me, so that nobody else can see or change my list.
36. As a person, I want the model unable to forget my logins itself, so that a page cannot talk the agent into revoking my sessions (or covering its tracks).
37. As a person, I want typed passwords never to appear in what the browse tools return, so that a field the agent or I filled does not leak into the history.
38. As a person, I want the agent never to act on the tab I'm logging in on, so that my typing and its actions don't collide.
39. As a person, I want my logged-in sessions to survive a Camoufox restart without the site seeing a new device, so that sites don't challenge or drop me after every deploy.
40. As the operator, I want the identity map and the public link base address as per-deployment values, so that the repo holds no personal names or hostnames beyond placeholders.
41. As the operator, I want remembered sessions encrypted at rest with a key from secrets, so that a Redis dump does not hand out my accounts.
42. As the operator, I want no model tool able to read the session store, so that the store is out of reach of prompt injection.
43. As the operator, I want the live view and list pages served under the assistants host's `/browse` path, so that they share WebChat's VPN-only exposure and certificate.
44. As the operator, I want the browse server to keep working with no identity map configured, so that a deployment that doesn't want this feature has it simply off.

## Implementation Decisions

**Identity**

- A **person** is a deployment-level name. An **identity map** — per-deployment values in the compose environment of the browse server, never `appsettings.json` — lists, per person, the user ids that are that person: a Telegram username, a WebChat name, an enrolled voice speaker. The browse server resolves the turn's `ConversationContext.UserId` through it. No match, or the voice `household` fallback, is no person.
- An empty map is the feature off: `asMe` and the handoff tool refuse with a message naming the reason, and nothing else changes.

**Browser contexts**

- The browser holds the existing **anonymous context** (shared, unchanged, carries no person's cookies) plus one **person context** per person, created on first use and restored from that person's remembered state.
- A browse session's tabs may live in either context. `web_browse` gains `asMe`; with it, the tab opens in the turn's person context, without it in the anonymous one. A tab never changes context. Reuse-before-open matches only tabs in the same context. Popups stay in their opener's context. Refs keep routing to the tab that stamped them, so `web_action`, `view_image`, `web_snapshot` and `back` need no flag. The tab cap and the idle clock are per browse session as today.
- Workers keep their parent's browse session (as today), and so reach the parent's `asMe` tabs; a worker's own `asMe` resolves the same person, since it carries the parent's conversation context.
- `asMe` on a site the person has never handed off still browses (in the person context, logged out); the model reads the page, as it does when a session has expired, and starts a handoff. The tool envelope names `asMe` in its result so the transcript shows it.
- No approval gate: the web tools stay whitelisted, `asMe` included.

**Fingerprint**

- One fingerprint is generated once, kept in a volume of the Camoufox service and passed to every launch, so every context presents the same device across restarts. Anonymous and person contexts share it; cookies, not fingerprints, separate them. A bump of the Camoufox package must keep the pinned fingerprint valid (the build fails loudly otherwise, like the existing anchor checks).

**Handoff**

- A new tool, the **handoff tool** (`web_login`, taking a url), opens that url in a fresh **handoff tab** in the person context — a tab outside the browse session's tab pool, so no ref, browse or action can reach it — and mints a **handoff ticket**: single-use, ten minutes, bound to the person, the conversation context and the tab. It answers with an absolute link built from a per-deployment public base address.
- On a text channel the model replies with the link. On voice the browse server also emits a fire into a new WebChat conversation for the person's WebChat name, whose prompt asks the agent to present the link; WebChat's existing end-of-stream Web Push carries the ping. The tool's answer tells nabu the link is in WebChat.
- The handoff tool refuses on a fire's turn (schedule, watch, the login wake itself); the conversation context gains the turn's origin kind so the server can tell. Workers don't get the handoff tool: it is removed from a subagent's tool set agent-side, since a worker's calls carry the parent's context and the server cannot tell them apart.
- Abandoned or expired: the ticket and handoff tab are dropped; nothing is emitted.

**Live view**

- Served by the browse server itself under the assistants host's `/browse` path (a new Caddy route). Opening the link spends the ticket and binds the page to a short-lived cookie for that one handoff; a second open is refused.
- Frames come from Playwright's page screencast of the handoff tab over a WebSocket; mouse, wheel and keyboard events come back the same way and are dispatched through the page's mouse and keyboard. Popups the handoff tab opens join the view.
- The page carries a Remember box (ticked) and Done.

**Done and the wake**

- Done saves (if remembered), closes the handoff tab and emits the **login wake**: a channel message with the original conversation id, agent id and user id, `ReplyTo` the original reply target (the voice satellite's address included), and a new origin kind, `Login`. Its prompt names the site and that the person is now logged in, so the model resumes the task.
- The browse server becomes a channel server (a tool server and a channel server, as Sugary is), with `BufferAlways` so a wake arriving while the agent reconnects is kept, and a `channelEndpoints` entry agent-side.
- The wake carries the original conversation id, so it joins the live conversation group and queues behind a running turn; a group that isn't live honours its `ReplyTo`. On voice with no live satellite session, the existing announcement path speaks it on that satellite.

**Remembered state**

- Stored in Redis (the browse server already holds a connection), one record per person: Playwright storage state (cookies, localStorage, IndexedDB) encrypted with a key from secrets (placeholder in `DockerCompose/.env`), plus the list of remembered and live-only sites with when each was handed off and last used.
- Unticked Remember marks the sites the handoff touched (registrable domains of every page the handoff tab visited) as live-only; every save of that person's state leaves their cookies and storage out. Live-only logins live in the person context in memory until the browser restarts.
- The person's state is re-saved, debounced, after `asMe` browses and actions, so rotated cookies persist. No expiry of our own.
- No model tool reads or writes the store.

**Logins page**

- A tool (`web_logins`) mints a one-time, ten-minute link for the asking person to a page under `/browse` listing their sites (remembered or live-only, last used) with Forget. Forget drops the site's cookies and storage from the person context and from the saved state. No model tool forgets.

**Password values**

- The accessibility snapshot reports a password field's value as masked, never the live value; that covers `web_snapshot`, the snapshot composed with `web_browse` and `web_action`'s diff. This applies to every context, not only person contexts.

## Testing Decisions

A good test drives a module through its outer surface and asserts what a caller or a person observes — what a tool answers, what the inbox receives, what a page shows — never which internal method ran.

- **The browse server's surface, with a fake browser.** A real host and a real MCP client sending the conversation context in `_meta` (prior art: `McpWebSearchSessionScopeTests`), plus the `/browse` HTTP endpoints driven directly and a drained channel inbox (prior art: `WatchFiredEndpointTests`, `ChannelInboxProbe`). Covers: identity map resolution and refusals; `asMe` reaching the browser with the right person; the handoff tool refusing on a fire's origin; ticket single use, expiry and binding; Done emitting the wake with the original conversation id, reply target and `Login` origin; abandonment emitting nothing; the voice handoff emitting the WebChat fire; the logins page listing and forgetting; the store never reachable from a tool.
- **`PlaywrightWebBrowser` against real Camoufox** (prior art: `Tests/Integration/Clients`, `HermeticPage`, the isolated pool). Covers: a cookie set in a person context absent from the anonymous context and from another person's; saved state restored after the browser reconnects; live-only sites left out of a save; refs routing across contexts within one browse session; popups staying in their opener's context; the handoff tab unreachable by refs; screencast frames arriving and forwarded typing landing in a field; a password field's value masked in snapshot and diff.
- **Existing contracts follow the registration.** Moving the browse server's row to the channel role brings it under `ChannelReceiveContractTests`; `DeliveryTargetResolverTests` and the voice announcement tests already cover how the wake is delivered once it reaches the agent.
- The fingerprint pin is checked where the Camoufox image is built and by an integration test that reads `navigator.userAgent` and screen size across two launches.
- The `web-browsing` skill gains `asMe`, the handoff tool and "logged out → hand off"; its claims get eval scenarios (prior art: the web eval fixtures), including a worker reporting a login wall and a fire reporting one instead of minting a link.

## Out of Scope

- Stored credentials filled by the server (a password manager, TOTP generated host-side).
- Driving the person's own browser through an outpost.
- Approval gates on logged-in actions.
- A fingerprint per person.
- Our own session expiry or retention window.
- Display-only channel messages; the voice link costs a WebChat turn.
- A fallback from voice to another channel when no satellite session is live.
- Passkeys: Playwright's virtual authenticator is visible to the page and the agent cannot use a person's existing passkeys.
- Authentication on WebChat's identity picker.

## Further Notes

- `docs/adr/0034` (tabs) and `docs/adr/0033` (images) stay intact: a ref still finds its own tab; contexts are one more thing a tab belongs to, invisible to the model.
- Persistent browser profiles are not available over Camoufox's server mode; storage state is the only persistence route.
- Canvas `toDataURL` hashes differ per context and launch (a random PNG chunk, stock Firefox); pixel readback is stable in the pinned build. A future Camoufox bump that adds canvas noise for a cross-OS identity could change this.
- `.claude/rules/web-browsing.md` gains a section on contexts, the handoff and the password rule when this lands.
