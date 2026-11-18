# 05: A person links their LibreLinkUp account

**What to build:** `GlucoseLink` (`IPersonal`) and migration; password sealed with AES-GCM under `Sugary:SealingKey` (secret: `.env` placeholder `SUGARY_SEALING_KEY`, compose env), associated data the owner's ids. Routes under `/api/glucose/link`: put (validates by logging in and listing connections; one connection chosen, several returned to choose), choose connection, settings (unit, target low/high), get (never the password), delete (forgets credentials, keeps readings). Writes go through `OneLogWriteAtATime`. Spec: "The link".

**Blocked by:** 04

Status: ready-for-agent

- [ ] Linking with refused credentials, a terms step or no connections answers a typed refusal the app can word.
- [ ] Two connections → the person chooses; the chosen `patientId` is kept.
- [ ] The sealed password round-trips only with the right owner and key; no route returns it.
- [ ] Unlink deletes the link and keeps readings; `MigrationsMatchTheModelTests` green.
