# 11 — Playlists and podcast episodes

**What to build:** the media rules that exist because guessing a name fails in ways the model
narrates confidently.

A playlist is browsed from the user's library before playback and played by its exact title,
never by the words the user spoke. A podcast show may play by name, but a specific episode plays
only by its exact playable uri, which must be looked up first.

**Blocked by:** 05, 06, 10.

**Status:** done

- [x] The Music Assistant fake serves a library whose playlist titles differ from the phrasing the
      scenario's user turn uses. "Mi música favorita" against a library holding
      "Liked Songs dethonv". The library itself is served by the Home Assistant fake, because
      `browse_media` is a Home Assistant service; the Music Assistant fake serves the one thing
      Home Assistant has no service for, the episode list.
- [x] Playing a playlist browses first and plays the exact title from the library, on the Music
      Assistant player rather than on the television standing beside it in the same room.
- [x] A playback call carrying the user's spoken phrasing as the title fails the scenario. It
      fails the way it fails in a real home: the fake answers an unresolvable name with the same
      bare 500, and the browse-and-retry that follows breaks the call ceiling.
- [x] A specific episode is looked up and played by its uri; playing the show name instead fails,
      because the required call matches the uri and nothing else. The player arrives playing a
      *different* episode of the same show: with the asked-for one loaded, its uri would be
      readable from `state.json` and the required listing would prove nothing about where the uri
      came from.
- [x] A resume-versus-restart case behaves as the contract states: "ponlo otra vez desde el
      principio" is a seek on the player that already has the item loaded, and any play at all is
      an unnecessary call.
- [ ] Every scenario cites its claims and was demonstrated red once. Two of four claims survived
      their demonstration and are cited; two did not and are exemptions now:
      - **Cited.** The playlist rule: deleted, the model played "mis favoritos", took the 500, and
        spent the rest of the turn on `browse_media.sh --help` and an empty browse.
      - **Cited.** The episode rule: deleted, the model read the player's state and answered
        without playing anything.
      - **Withdrawn.** Which player music goes to: the paragraph was deleted and the playlist
        still went to the speaker rather than the television. Their names teach it.
      - **Withdrawn.** Restart-is-a-seek: deleted, the seek still happened.

**The episode scenario asks for two of four runs** rather than two of three: on about a third of
them the lookup goes to a worker instead of being done, which is the delegation reflex recorded in
`ClaimExemptions`. The threshold says the behaviour has to hold at least half the time rather than
being retried until it does.

**Harness change this ticket forced:** a permission can now be scoped by the command it ran
(`CallPermission.Manual`). The first two demonstrations were red for the wrong reason — the model
read an action's `--help`, which the scenario did not tolerate — and a scenario that cannot
distinguish reading the manual from running the action is measuring the wrong thing.
