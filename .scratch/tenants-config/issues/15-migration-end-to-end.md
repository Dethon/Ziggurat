# 15: The migration end to end

**What to build:** Each earlier ticket brought its own household-migration step; this one proves them together. The whole household migration runs over a copy of real-shaped data (every shared store, the agents' and chat client's settings, the bots): a dry run reports everything that would move and writes nothing, a run moves it all, a rerun changes nothing; the deploy notes list the order. Spec: `../spec.md` ("The migration").

**Blocked by:** 05, 06, 09, 10, 11, 12, 13

**Status:** done

- [x] A dry run over the copy reports every step's moves and writes nothing
- [x] A run leaves the home household with its configuration and every shared store re-keyed, and a signed-in person sees their topics, schedules and spaces
- [x] A rerun changes nothing and says so
- [x] The deploy notes list the steps in order (`../deploy.md`, and the launch-stack skill's migration section)

**Done:** `HouseholdMigrationCommandTests` runs the whole migration over `SeedTheHomeAsync` (every shared
store in its old shape, two spaces with a topic and a phone each, a due schedule, the old search index,
the people's own stores, the home's keys): the dry run names a move under every step and counts exactly
what the run makes; after the run the home is read through the stores and the accounts service's views
(the hub's spaces on a signed-in assertion); a rerun changes nothing. The ordering problem between 08 and
13 is fixed both ways: the migration seeds Idealista's pair beside the bots from one piped input
(`--home-keys -`, replacing `--telegram-bots`), and the seed route gives a saved keys section only the
keys it lacks, so a key set by hand first is kept and the rest are added.
