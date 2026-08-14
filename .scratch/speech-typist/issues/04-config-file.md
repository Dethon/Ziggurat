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
`&stt-model` anchor cannot reach this file, and what a mismatch actually costs.

Every later ticket adds its own keys to this file rather than a wiring-up ticket at the end.

**Blocked by:** 03.

**Status:** resolved

- [x] A `config.toml` beside the executable is used in preference to the one in the user profile
- [x] With neither present, one is written to the user profile with commented defaults, and the
      program starts working
- [x] The Lemonade base URL, model and request timeout come from config, not the environment
- [x] The base URL default names the Lemonade host rather than the compose-internal name
- [x] The model's comment records the hand-maintained agreement with `STT_MODEL` and what a
      mismatch costs (a 409, not a slow first dictation — see the comments)
- [x] A malformed config is reported clearly and does not leave the program half-configured
- [x] Config precedence and the first-run write are covered by tests that need no Windows

## Comments

2026-08-14 — Done, and two defaults were corrected against what the rest of the repo actually
pins: the base URL ends at `/v1` (Lemonade serves health under `/api/v1`, and the wrong one
parses fine and 404s only mid-sentence), and the model is `Whisper-Large-v3-Turbo`, which is
what compose's `&stt-model` anchor warms. Both have their own test.

The file written on first run and `Config::default()` are two spellings of the same settings,
and a test fails if either drifts from the other.

2026-08-14 (later) — Reopened briefly on real hardware: the first dictation failed with
`Dictation failed: Lemonade answered 409`. The default model name was wrong, and so was the
belief behind it. Lemonade holds one transcription model at a time and the deployed one is
pinned, so naming any other model is refused outright — `slots_pinned_error`, "All loaded models
of type transcription are pinned. Unload a model first." — rather than lazily pulled at the cost
of a slow start. The default is now `Whisper-Large-v3-Turbo-ES`, what the instance actually has
loaded, and the client surfaces the server's own message instead of a bare status code, because
"Lemonade answered 409" tells a person nothing about what to change.
