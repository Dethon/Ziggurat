# 04: Setting up or revoking a server takes effect on the next turn

**What to build:** Minting and revoking a tunnel credential announce a change of the household's
configuration, as saving a section does, so the agent drops its cached view of the household at once.
An owner who adds a server and starts it, or revokes one, sees their agent follow from its next turn
rather than within the cache's lifetime. Spec: `../spec.md` ("the accounts service says it").

**Blocked by:** 02 (a household's agents reach the vault it set up)

**Status:** done

- [x] Minting a credential announces a configuration change for its household; so does revoking one (household configuration tests)
- [x] An outpost credential's mint or revoke announces nothing about own servers, or announces harmlessly — the agent's view is unchanged by it
- [x] Turn-level: a credential minted between two turns of one conversation is reached on the second turn (`MultiAgentFactoryHouseholdTests.ASetUpChangedBetweenTwoTurnsOfAConversation_IsFollowedByTheSecond`, from ticket 02; the reader drops a household on any announcement naming it)
