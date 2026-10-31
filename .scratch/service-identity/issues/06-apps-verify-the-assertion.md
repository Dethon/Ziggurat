# 06: Apps behind the proxy verify the assertion

**What to build:** A container on the internal network can no longer forge a person to the apps. The chat hub, Sugary, the dashboard and the agent registration API verify the signed assertion against the accounts service's keys and take the person from it, not from the plain headers alone; a missing or invalid assertion is signed out. The registration API takes the assertion SexyTime's server forwards, beside its secret, and the SexyTime change is noted for its own repository. Spec: `../spec.md` ("Apps behind the proxy").

**Blocked by:** 03

**Status:** done

- [x] A request with valid plain headers but no assertion is treated as signed out by each app
- [x] A request whose assertion names one person and whose plain headers name another acts for the assertion's
- [x] The registration API refuses a call with the secret but no valid assertion
- [x] The note for SexyTime's repository says what to forward (`../sexytime-note.md`)

Each app also holds the assertion to its own channel (`signalr`, `sugary`, `dashboard`, `sexytime`):
one signed for another app's route, or the guest's, is signed out too. The dashboard additionally
answers a verified non-operator 403. The chat hub verifies once per connection, as it read the
headers once before. `npm run dev` for Sugary asks the local accounts service's session check with
a `mint-session` cookie (`SUGARY_SESSION`) rather than naming a person itself.
