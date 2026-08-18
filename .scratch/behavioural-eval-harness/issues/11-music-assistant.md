# 11 — Playlists and podcast episodes

**What to build:** the media rules that exist because guessing a name fails in ways the model
narrates confidently.

A playlist is browsed from the user's library before playback and played by its exact title,
never by the words the user spoke. A podcast show may play by name, but a specific episode plays
only by its exact playable uri, which must be looked up first.

**Blocked by:** 05, 06, 10.

**Status:** ready-for-agent

- [ ] The Music Assistant fake serves a library whose playlist titles differ from the phrasing the
      scenario's user turn uses.
- [ ] Playing a playlist browses first and plays the exact title from the library.
- [ ] A playback call carrying the user's spoken phrasing as the title fails the scenario.
- [ ] A specific episode is looked up and played by its uri; playing the show name instead fails.
- [ ] A resume-versus-restart case behaves as the contract states.
- [ ] Every scenario cites its claims and was demonstrated red once.
