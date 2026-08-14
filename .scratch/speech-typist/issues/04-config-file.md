# 04 — Config file

**What to build:** the speech typist reads its settings from a TOML file instead of the environment,
and writes one for you the first time it runs so you can see what is configurable without reading
the source.

A `config.toml` sitting beside the executable wins, which is what keeps the single-file
distribution genuinely portable — copy the executable and its config to another machine and the
settings travel. Failing that, the one under the current user's application-data directory is used,
and that is the one created on first run, with the defaults written out and commented.

This ticket takes over what ticket 02 read from the environment — the Lemonade base URL, the model,
the request timeout — and adds the microphone name override and the binding. The base URL default
names the Lemonade host, not the compose-internal `lemonade:13305`, which means nothing from a
Windows desktop. The model default is the one the container is warmed with, and the comment beside
it says plainly that it must be kept in agreement with `STT_MODEL` by hand, that compose's
`&stt-model` anchor cannot reach this file, and that a mismatch shows up as a slow first dictation
rather than an error.

Every later ticket adds its own keys to this file rather than a wiring-up ticket at the end.

**Blocked by:** 03.

**Status:** ready-for-agent

- [ ] A `config.toml` beside the executable is used in preference to the one in the user profile
- [ ] With neither present, one is written to the user profile with commented defaults, and the
      program starts working
- [ ] The Lemonade base URL, model and request timeout come from config, not the environment
- [ ] The base URL default names the Lemonade host rather than the compose-internal name
- [ ] The model's comment records the hand-maintained agreement with `STT_MODEL` and the
      slow-first-dictation symptom of a mismatch
- [ ] A malformed config is reported clearly and does not leave the program half-configured
- [ ] Config precedence and the first-run write are covered by tests that need no Windows
