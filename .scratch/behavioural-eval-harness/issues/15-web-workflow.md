# 15 — Web workflow: search, browse, snapshot, act

**What to build:** a web request goes through search, opening the page, reading it, and only then
acting — against a site the harness serves, so the pages hold still and can be authored to
contain the exact trap the claim is about.

**Blocked by:** 05, 06.

**Status:** done

- [x] The test host serves a static site, and a fake search server returns results pointing into
      it. Three pages — a recipe, a museum's opening hours, a booking form — and Brave's own
      response shape through Brave's own client. The websearch server runs in process with both of
      its externals replaced and nothing else: search answers from a table, and the browser is a
      locally launched Chromium instead of the remote hardened Firefox a deployment connects to.
- [x] A research turn searches, opens a result, reads it, and answers from what it read. The
      resting time is in the article and deliberately not in the snippet, so an answer carrying it
      is an answer from a page that was loaded.
- [x] An action turn snapshots the page before acting on it; acting first fails the scenario. The
      required call is the browse that carries `snapshot: true` — the single call the contract asks
      for — and the action must name a ref of the shape that snapshot hands out, so acting on a
      page whose structure was never fetched cannot pass. The proof that the booking went through
      is the confirmation code, and there is one code per turn: booking Sunday returns a different
      one, so a reply carrying the Saturday code is a reply about the turn the user asked for.
- [x] A page whose content contradicts the search snippet is answered from the page: the snippet
      still advertises the old opening time and the page's first line corrects it.
- [x] No scenario in this family reaches the public internet, and it is the browser that
      guarantees it rather than a rule somebody has to remember: Chromium is launched behind a
      proxy that is not listening, bypassed only for loopback. A fixture test proves a public page
      cannot load while the served site can.
- [ ] Every scenario cites its claims and was demonstrated red once. One of four cited claims
      survived; the other three are exemptions:
      - **Cited.** Refs come from a snapshot: deleting the interaction workflow turned the booking
        red on both runs. What the model did instead of snapshotting was hand the whole booking to
        a worker — the same reflex ticket 16 recorded — so without that prose it does not attempt
        the interaction at all.
      - **Withdrawn.** A url comes from a search: not falsifiable here. The served pages live on a
        loopback address and a random port, so searching is forced by the fixture.
      - **Withdrawn.** The answer comes from what was read: both sentences deleted, and the museum
        answer still came from the page rather than from the snippet.
      - **Withdrawn.** Urls are cited only in writing: deleted, and the spoken reply still carried
        no url — the voice section forbids one on the same turn.

**The turn says to act rather than to look into it** — "abre el formulario … y reserva" rather
than "reserva una plaza en el taller del barrio". Phrased as research, the booking goes to a worker
on about half the runs, which is the reflex the delegation exemptions already record; this scenario
is about the refs, not about that decision.

**Two things this ticket changed in the harness.** A required call is matched by pattern rather
than by name, because a tool served over MCP is named after the endpoint it was dialled on — host
and port — and the port is whatever was free. And the booking scenario names the site rather than
describing it: "el taller del barrio" reads as research, and research is handed to a worker about
half the time, which would have made the scenario a measurement of that reflex.

**One fixture defect worth recording**, found by the first failing run rather than by reading the
code: the form originally answered its own POST with the confirmation, so the code lived at the
form's url and every later read of that url returned the empty form. The model did everything
right and reported, correctly, that the page showed no code. It is post/redirect/get now.
