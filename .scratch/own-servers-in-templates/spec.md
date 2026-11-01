# A household's agents reach the own servers it set up

Status: ready-for-agent

## Problem Statement

An owner of a household other than the home can add one of the household's **own servers** on the
admin area's Servers page: they name it (`vault`), are shown its tunnel credential once, start the
**tenant kit**'s vault at home with it, and the page then shows the server connected, with its tools.
Their household's agents never use it. The agents of a new household are built from the shipped
**agent templates**, and the barebones Jonas template names only the deployment's **shared servers**;
an owner can change an agent's name and custom instructions and nothing else. So the vault is
connected and idle, the agent answers "I have no vault mounted", and nothing tells the owner why. The
only own servers such a household's agents reach today are **outposts**, which every agent that uses
outposts picks up by themselves.

The template names none of them on purpose: a server an agent names that is not connected is listed
on every one of its turns as offline, and a household that never set up a vault should not be told
on every turn that its vault is off.

Found in the manual test of 2026-11-02 (web-identity, smoke stack): a second household minted a
`vault` credential, its kit vault connected, and its agent could not read the note in it.

## Solution

The barebones Jonas template names the own servers the tenant kit runs for a household — the vault,
the sandbox, web search and the printer — and a household's agents reach **only the ones the household
has set up**: a server name for which the household holds a live tunnel credential. A server the
household never set up is left out of its agents entirely, so nothing is said about it; one it set up
and that is not connected right now is offline for the turn, exactly as the home's own servers are
today, and its tools come back on the turn after it reconnects. The home household is unchanged: its
seeded agents name the servers its kit runs and treat every one of them as set up.

So an owner who adds a vault on the Servers page and starts it at home finds their agent reading
their notes on the next turn, and an owner who never adds one never hears of it.

## User Stories

1. As an owner of a new household, I want my agent to use the vault I added on the Servers page, so that it can read and write my notes.
2. As an owner, I want my agent to use the sandbox I added, so that it can run commands and work on files for me.
3. As an owner, I want my agent to use the web search I added, so that it can search and read web pages from my own connection.
4. As an owner, I want my agent to use the printer I added, so that it can print documents at home.
5. As an owner who has not added a vault, I want my agent never to mention a vault, so that I am not told daily about something I never set up.
6. As an owner who has not added any server, I want my agent to behave exactly as the barebones agent does today, so that nothing gets worse for households that use only the shared servers.
7. As an owner, I want a server I added and then switched off to be reported as offline on the turn that needs it, so that I know to switch it back on.
8. As an owner, I want a server I switched back on to be used again from the next turn, without anyone restarting anything, so that a reboot at home costs me nothing.
9. As an owner, I want a server whose credential I revoked to be gone from my agents from the next turn, so that a lost machine is not even asked for.
10. As an owner, I want a server I add while a conversation is open to be reached from that conversation's next turn, so that I do not have to start a new conversation to use it.
11. As an owner, I want every agent of my household made from the Jonas template — including ones I added and renamed — to reach the servers I set up, so that naming an agent does not change what it can do.
12. As an owner, I want a worker my agent hands part of a task to reach the same set-up servers as my agent and no others, so that a long task can use my vault and sandbox too.
13. As an owner, I want the Sugary template's agent to keep reaching no own server, so that carb estimates stay as they are.
14. As an owner, I want my agent to say which of my servers is offline when a request needs it, so that I can tell an off machine from a missing feature.
15. As an owner, I want my household's servers to be reached only through my household's tunnels, so that my agent never reaches another household's vault of the same name.
16. As a member of a household, I want the household's agents to use the household's servers on my turns too, so that what the owner set up works for everyone in it.
17. As an owner, I want turning a feature off (schedules, timers, Sugary, Idealista, memory, workers) to keep working as it does, independently of which own servers I set up, so that features and servers stay two separate choices.
18. As the operator, I want the home household's agents to keep naming and reaching the home's own servers exactly as today, so that this change cannot take a tool away from the home.
19. As the operator, I want a suspended household's servers to stay refused as today, so that suspension still stops everything.
20. As the operator, I want the deployment to learn which servers a household set up from the accounts service, where the credentials are, so that there is one source of truth and nothing new to configure.
21. As the operator, I want minting or revoking a credential to reach the household's agents within the same bound as any other change to a household's configuration, so that I can predict when it takes effect.
22. As a developer, I want "set up" to mean exactly "holds a live tunnel credential for that server name", so that the rule is checkable without asking whether a machine is on.
23. As a developer, I want an outpost's credential not to count as setting up an own server of the same name, so that a machine called `vault` does not make the household's agents dial a vault server.
24. As a developer, I want servers the household has not set up withheld the way a feature turned off withholds a shared server, so that there is one mechanism for "this household's agents do not reach this server".
25. As a developer, I want the template's prompt snapshots to show the new servers, so that a reviewer sees what a new household's agent is now told.

## Implementation Decisions

- **What "set up" means.** A household has set up an own server of a given name when it holds a live
  (not revoked) tunnel credential minted for that name. Outpost credentials (`outpost-<NAME>`) never
  count. Whether the server is connected right now is a different question, answered at each turn's
  start as today.
- **The accounts service says it, in the agent's view of the household.** The view the agent already
  reads per household (its settings, agents, features, spaces, key, disabled people, suspension)
  gains the household's set-up own server names, read from the same credential store the Servers
  page lists. Minting and revoking a credential announce a change of the household's configuration,
  as saving a section does, so the agent drops its cached view; the agent's cache bound
  (`Accounts:ConfigurationLifetime`) is otherwise the most it takes.
- **The agent withholds what was not set up, as it withholds a feature's servers.** Building a
  household's agents from its view already withholds the shared servers its features turned off,
  from every definition and from every worker its agents spawn. Own server names the household has
  not set up join that: they are removed from the household's agent definitions and carried to
  workers the same way, so they are never resolved, never dialled and never named offline. A removed
  name is not reported anywhere. The home household withholds no own server: its seeded definitions
  name what its kit runs.
- **Offline stays what it is.** A set-up server that is not connected at a turn's start is left out of
  the turn and named on the turn's offline line, and a reconnection is picked up at the next turn's
  start (the tunnel's existing rules, ADR 0012, and the exception to ADR 0027 for own servers).
- **The Jonas template names the kit's servers.** Its server list adds `vault`, `sandbox`,
  `websearch` and `printer` beside its shared servers, with what the home's seeded Jonas pairs them
  with: the web search tools on its whitelist, the filesystem feature it already has. Home Assistant,
  voice and the library stay out of the template. The Sugary template is unchanged.
- **Nothing changes for owners' editing rights.** Owners still edit only an agent's name and custom
  instructions; which servers an agent reaches is the template's list filtered by what the household
  set up.
- **Workers** inherit the parent's withheld list as they inherit its household today, so a worker
  definition that names the vault (the Jonas worker) reaches it only where the parent's household set
  it up.

## Testing Decisions

- Good tests here drive the household from the outside — a view of the household as the accounts
  service would answer it, and a turn — and assert what the agent was given and told: its tools, its
  mounts, its offline line. They do not assert on the withheld list or on private helpers.
- **Seam 1, the household's agents at turn level** (prior art: the multi-agent factory's household
  tests, which build a household's agents from a view and run a turn against a fake model; and the
  own-server agent tests, which host own servers in process and switch them on and off between
  turns). Cases: a household with `vault` set up and connected reaches it; set up and not connected
  names it offline; not set up neither reaches it nor names it; revoked since the last turn is gone
  next turn; the home with its seeded definition is unchanged; a worker spawned by a household that
  did not set up the vault does not reach it. Edge cases of the definition filtering (several agents,
  a renamed agent, the Sugary template untouched, features and servers withheld together) go in the
  household agents' unit tests.
- **Seam 2, the accounts service's agent view** (prior art: the household configuration tests). Cases:
  the view lists the names of live credentials; a revoked credential's name is gone; an outpost's
  credential is never listed; minting and revoking each announce a configuration change for that
  household.
- **Seam 3, the smoke tier** (prior art: the smoke test that onboards a household end to end). A new
  household mints a `vault` credential through the admin API, a kit vault is started with it on the
  smoke stack, and the household's agent reads a note through it. The smoke kit's own vault is the
  home's, so this needs a second vault for the new household.
- Prompt snapshots for the Jonas template regenerate. The eval's changed tier selects on prompt
  sections, skills and scenario files, so a template's server list selects nothing; run the family
  whose scenarios use the template's agent, or the full tier, if its prompt changes.

## Out of Scope

- Home Assistant and voice for households other than the home: both are channels the deployment
  follows for the home alone today.
- The library in the template: a household that adds a `library` credential still reaches it with no
  agent until a template names it.
- Web search's login handoff and browse pages for a household other than the home: a browse link
  names no household yet (`.claude/rules/tunnel.md`, "Which household"), so `web_login` from such a
  household's agent cannot hand off. Searching and reading pages work.
- Letting owners choose servers per agent, or edit an agent's server list.
- Telling owners on the Servers page which of their servers no agent uses.

## Further Notes

- The rule turns the template comment's reason for naming nothing into the reason for this filter: a
  server is spoken of only once the household set it up.
- Deploy: the accounts service and the agent ship together; an agent reading a view without the new
  field treats it as nothing set up, which is today's behaviour for every non-home household.
