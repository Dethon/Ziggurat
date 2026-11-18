# 09: Import a person's glucose from Home Assistant

**What to build:** `SugaryCommand import-glucose --ha <url> --token <file> --entity <id> --tenant <tid> --user <uid> [--days N]`: recorder history in ten-day windows (`minimal_response`, numeric states only), then hourly long-term statistics means (WebSocket `recorder/statistics_during_period`) for days before the history; `source=import` / `import-hourly`; skip within 2.5 min of an existing reading; print counts. Imported readings change no watch state. Spec: "Import from Home Assistant".

**Blocked by:** 06

Status: ready-for-agent

- [ ] Against `FakeHomeAssistant`/the HA test container: a run imports, a second run imports nothing.
- [ ] Non-numeric states (`unavailable`) are skipped; stamps are UTC instants.
- [ ] The token is read from a file, never an argument value, and never logged.
