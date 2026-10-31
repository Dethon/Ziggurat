# Tenants 4c: admin and onboarding

Status: ready-for-agent

## Problem Statement

After 4a and 4b a household can have its own servers, its own configuration and its own agents, but
only the operator can give it any of them, by running commands on the deployment's machine with
files in hand. An owner cannot invite a member, set the household's OpenRouter key, rename its
agent, arrange its spaces or connect a server of its own without asking the operator. The operator
cannot create a household or stop one without a shell, and cannot see who uses what. A key that is
mistyped is found out only when a turn fails.

## Solution

Owners run their household from an admin area inside the chat client, and the operator runs the
deployment's households from the dashboard. Both talk to an admin API on the accounts service, which
owns people and configuration and checks the role from the verified assertion on every request.

The operator creates a household and gets its owner's sign-in link. The owner's first sign-in lands
in the admin area, where they set the household's OpenRouter key — checked with the provider before
it is saved — name the household's agent and invite their people. From then on the owner manages
members, keys, features, agents (name and custom instructions, with a short history), spaces and the
household's own servers, and sees what the household has used. The operator can suspend a household,
which stops everything it does without deleting anything.

## User Stories

### The household's owner

1. As an owner, I want to land in the admin area on my first sign-in when my household has no
   OpenRouter key, so that I know what to do first.
2. As an owner, I want to enter my household's OpenRouter key and be told at once whether it works,
   so that I do not find out from a failed turn.
3. As an owner, I want a key that the provider rejects not to be saved, and one I could not check to
   be saved with a warning, so that a provider outage does not block me.
4. As an owner, I want to see each key's state (working, rejected, unchecked) and when it was last
   checked, so that I know whether my household's agents can run.
5. As an owner, I want a key that starts failing in use to show as rejected, so that I see the
   problem before my people tell me.
6. As an owner, I want to enter my household's Idealista keys and Telegram bot tokens the same way,
   so that every key is managed in one place.
7. As an owner, I want never to see a saved key again in the clear, only its state and last
   characters, so that the admin area cannot leak it.
8. As an owner, I want to invite a person by a passkey link or a Telegram code, so that they can join
   my household.
9. As an owner, I want to set a member's role and whether they see all conversations, so that I
   decide who manages and who oversees.
10. As an owner, I want to disable a member, so that a person who left can no longer sign in, be
    recognised by voice or Telegram, or have schedules fire for them.
11. As an owner, I want a disabled member's memories, topics and log kept, so that disabling is
    reversible.
12. As an owner, I want to re-enable a disabled member or send them a recovery link, so that a lost
    passkey or a mistake is fixable.
13. As an owner, I want to rename each of my household's agents and edit its custom instructions, so
    that the assistant fits my household.
14. As an owner, I want to see the last few versions of an agent's name and instructions and restore
    one, so that an edit I regret is one click away.
15. As an owner, I want to add an agent from a shipped template and remove one, so that my household
    has the agents it needs.
16. As an owner, I want to turn my household's features on or off, so that our agents offer only
    what we use.
17. As an owner, I want to add, rename, recolour, reorder and remove spaces and pick each one's
    default agent, so that the chat client is organised our way.
18. As an owner, I want to add one of my household's own servers by name and get its tunnel
    credential once, so that I can start it at home.
19. As an owner, I want to see each of my servers' state — connected or not, since when, and the
    tools it offers — so that I can tell a dead server from a quiet one.
20. As an owner, I want to revoke a server's credential and have it disconnect at once, so that a
    lost machine cannot keep acting as my household's server.
21. As an owner, I want to see my household's use — tokens and cost per agent and per person, and
    Lemonade's speech and embeddings — by month, so that I know what we spend.
22. As an owner, I want to set my household's name, time zone and language, so that dates and
    replies are right for us.

### Members

23. As a member, I want no admin area offered to me, so that the chat client stays simple.
24. As a member whose household is suspended, I want a clear message instead of a silent failure,
    so that I know it is not my phone.

### The operator

25. As the operator, I want to create a household from the dashboard and get its owner's sign-in
    link, so that onboarding a household needs no shell.
26. As the operator, I want a list of households with their owners, members, servers connected and
    last activity, so that I can see the deployment at a glance.
27. As the operator, I want to suspend a household so that its sessions, turns, fires and tunnels are
    all refused, and resume it later, so that I can stop a household without deleting it.
28. As the operator, I want every dashboard page filterable by household, so that I can see one
    household's tokens, errors and latency.
29. As the operator, I want to mint a recovery link for any household's owner, so that a household
    whose only owner lost their passkeys is not locked out.
30. As the operator, I want every admin action recorded with who did it and when, so that a change I
    did not make can be traced.

### Developers

31. As a developer, I want every admin request checked against the role of the verified assertion
    for that one household, so that no page or client can act on another household.
32. As a developer, I want the admin API to reuse the configuration sections and their validation
    from 4b, so that a value set from the admin area and one set by the operator's command are held to
    the same rules.
33. As a developer, I want key probes behind one contract per provider, so that adding a provider is
    one probe.

## Implementation Decisions

### Where it lives

- The owner's admin area is a set of pages in the chat client, offered only to owners and behind the
  same session check. They call an admin API on the accounts service under `/accounts/admin/*`
  (same origin through the proxy). The operator's household list lives in the dashboard and calls
  the same API with the operator role. The internal API stays internal.
- Every admin request is checked against the verified assertion: the owner role of the household it
  acts on, or the operator role. Every change is recorded (who, what, when) in the accounts store.

### Onboarding

- The operator creates a household (name, owner's display name) in the dashboard and gets the
  owner's passkey link. The household starts with 4b's defaults: the two shipped templates, one
  space, the deployment's default features.
- An owner whose household has no OpenRouter key lands in the admin area on sign-in; until a key is
  set its agents refuse turns with 4b's message.

### Keys and probes

- Saving a key probes it through a per-provider probe: OpenRouter (the key's own info), Idealista
  (its token exchange), Telegram (the bot's identity). A definite authentication failure refuses the
  save; an unreachable provider saves it with a warning. A key's state (working, rejected,
  unchecked) and last check are stored beside it; an authentication failure in use marks it rejected
  and re-probes. A saved key is never returned, only its state and last characters.

### Members

- Owners invite by passkey link or Telegram code (the accounts invites), set a member's role and
  sees-all, disable and re-enable a member, and mint a member's recovery link. Disabling revokes the
  member's sessions, stops their links and Telegram and voice identities resolving, and stops
  delegations naming them from acting; their data stays.

### Agents

- Per agent: rename and edit custom instructions within a size limit; the last few versions are kept
  and one can be restored; an agent is added from a shipped template or removed. Nothing else about
  an agent is editable.

### Features, spaces, settings

- Features toggle 4b's shared servers and domain features for the household. Spaces are edited as
  4b's spaces section (name, slug, colour, order, default agent); a removed space's topics stay
  reachable from the default space. Settings are the household's name, time zone and language.

### Own servers

- An owner adds a server by name and gets its tunnel credential once (4a's credentials), sees each
  server's connection state, connection time and tools from the relay, and revokes a credential,
  which closes the tunnel.

### Usage

- A usage view per household and month: tokens and cost per agent and per person, and Lemonade's
  speech seconds, characters spoken and embeddings, read from 4b's per-household metrics. The
  dashboard's existing pages gain a household filter.

### Suspension

- The operator suspends and resumes a household. While suspended, its sessions are refused at the
  session check, its turns and fires are refused by the agent with a message where a person is
  waiting, and its tunnels are refused and closed. Nothing is deleted.

## Testing Decisions

A good test checks what a person or caller observes: which admin actions succeed for whom, what a
probe decides, what a suspended household gets. It never checks the admin pages' markup beyond what
a person reads and clicks.

Five seams:

1. **The accounts admin API (existing surface).** Every action refused without the owner role of
   that household or the operator role; members disabled and re-enabled; keys probed, refused or
   saved with a warning, and never returned; a server credential minted once and revoked; a household
   created and suspended; changes recorded. Prior art: the accounts HTTP tests.
2. **The chat client's admin pages (existing E2E stack).** An owner sets a key, names an agent and
   restores a version, edits a space, mints a server credential; a member sees no admin area. Prior
   art: the signed-in WebChat E2E tests.
3. **The dashboard (existing E2E).** The operator creates a household, gets the owner's link,
   suspends it; pages filter by household. Prior art: the dashboard E2E tests.
4. **Suspension (existing agent and channel seams).** A suspended household's sessions, turns, fires
   and tunnels refused, with a message where a person waits. Prior art: `SessionCheckTests`,
   `ChatMonitorAssertionTests`, `ChatMonitorDelegationTests`, and 4a's tunnel round-trip tests.
5. **The smoke tier (existing).** A fresh household onboarded end to end on a real stack: created,
   owner signed in, key set (a probe against a stub), agent named, a reply. Prior art:
   `StackSmokeTests`.

## Out of Scope

- Editing prompt sections, skills, model or servers of an agent.
- Deleting or exporting a household or a member's data.
- Spend limits and alerts.
- Self-service sign-up.
- Evaluating a household's agents.

## Further Notes

- Phase 4c of the multi-tenant plan ("Tenants decisions"), after 4a (the tunnel) and 4b (household
  configuration). It branches from `web-identity` and merges back into it; master then takes every
  phase together. The glossary's Households section names its terms.
- ADRs it touches: *The two browser clients stay separate* (0008) is kept — the owner's admin area
  is part of the chat client and the operator's household list part of the dashboard; neither
  borrows from the other.
- The admin pages follow the chat client's UI rules: icons are drawn inline, never typed.
