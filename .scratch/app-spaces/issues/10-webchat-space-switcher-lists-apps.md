# 10: WebChat space switcher lists apps

Spec: `.scratch/app-spaces/spec.md` ("Spaces and WebChat"); Testing Decisions, seam 4.

**What to build:** A person finds their apps where they find their spaces. A new hub method answers chat spaces, app spaces with their URLs, and the person's own published apps that aren't app spaces, marked private. The hub gets the person's apps from the apps server. The switcher uses the new method, and picking an app opens its origin. `GetSpaces` keeps its old answer for installed clients from before.

**Blocked by:** 06, 09

**Status:** ready-for-agent

- [ ] The switcher shows app spaces among chat spaces in the household's order, and the person's private apps marked private.
- [ ] Picking an app opens its origin. The app's icon is drawn, never typed.
- [ ] `GetSpaces` answers exactly what it did before.
- [ ] An E2E collection of its own, with the apps origin on a local wildcard name: a signed-in member sees an app space, opens it, is signed in through the app sign-in, and the app renders data read through `/_vfs/*`. A second household's member gets a 404.
