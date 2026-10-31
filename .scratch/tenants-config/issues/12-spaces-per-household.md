# 12: Spaces per household

**What to build:** Spaces come from the household's spaces section (name, slug, colour, default agent): the chat client serves the household's spaces, the hub admits only a space of the connection's household, a space opens on its default agent unless the person picked another, and a new household starts with one default space. Spec: `../spec.md` ("Channels").

**Blocked by:** 05

**Status:** done

- [x] A person sees only their household's spaces with their names and colours
- [x] The hub refuses a space of another household
- [x] A space opens on its default agent
- [x] A new household has one default space
- [x] A household-migration step seeds the home household's spaces from the chat client's list (through `POST /accounts/internal/household-config-seed`, added here for `CALLERS__household-migration`; 06 can seed its sections through it)
