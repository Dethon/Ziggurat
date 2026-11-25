# 09: App spaces

Spec: `.scratch/app-spaces/spec.md` ("Spaces and WebChat").

**What to build:** An owner shares a member's published app with the whole household. `HouseholdSpace` gains an optional built-app id. The spaces admin page lets an owner add an app space by picking one of the household's published apps, after seeing its author, `reads` and `connect`. Removing the space unshares the app, which stays its author's. While an app is an app space, every member of the household who is not disabled may open its published origin.

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] The admin page asks the apps server for the household's published apps, and shows author, mounts and origins before adding one.
- [ ] An app space's name and colour are the app's. It holds no topics, and `JoinSpace` on it answers "No such space."
- [ ] While shared, other members open the published origin, a disabled member gets a 404, and another household's member gets a 404.
- [ ] Removing the app space unshares the app: only its author can open it again.
- [ ] Deleting an app that is an app space is refused and names the space.
- [ ] An owner orders app spaces among chat spaces.
- [ ] Covered by spaces admin integration tests and apps server access tests.
