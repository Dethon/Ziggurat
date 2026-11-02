# A household's own channel servers answer it

Status: ready-for-agent

## Problem Statement

A household's own server can also be a **channel**: voice (satellites in the house), Home Assistant
(watches that start a turn), the library (a finished download that starts a turn) and web search's
channel half (a finished login that wakes its conversation, browse pages opened from a link). The
agent follows each of these **once for the whole deployment, and only the home household's tunnel of
that name**. A household other than the home can mint `voice` on its Servers page and start the
tenant kit's voice server with it. The relay admits the tunnel, the satellite hears a question and
the transcript is made, and then nothing happens: no agent reads that server's inbox, and the
satellite waits out its 120-second reply handshake in silence. The same household's Home Assistant
watches never fire a turn, its library never says a download finished, a login it hands off through
its own web search never wakes the conversation, and a browse link opens the home's web search.

Even with the inbox read, the turn would be refused: the voice server addresses the agent `nabu`,
only the home has one, and no template makes a voice agent for anyone else.

Found in the manual test of 2026-11-02 (web-identity, smoke stack). The smoke script minted the kit's
voice credential for a non-home household by mistake (fixed in `679ed90ea`), and the voice check timed
out. Today it is documented as out of scope ("channels the deployment follows for the home alone",
`Domain/Prompts/Agents/jonas.md`, `.claude/rules/tunnel.md` "A household's channel server").

## Solution

The agent follows **every household's** channel servers. Each opening of a household's tunnel for a
channel server is a connection of that household's. A message read from it is believed only for that
household, and what goes out on it (a reply, a fire's delivery, an approval, an alert, an attachment
fetch) is only ever that household's. The deployment's own channels (the chat client and Telegram,
dialled by URL) keep serving every household, as now.

A new **voice agent template** gives a household an agent that answers aloud, made the way an owner
adds any agent. The voice server addresses it as it addresses the home's Nabu today. A household with
voice and no voice agent hears a short spoken refusal instead of silence.

The other channel servers then work for any household with no change of their own: watches fire,
downloads announce, logins wake their conversation, and browse links open the household's own web
search.

## User Stories

1. As an owner, I want to ask my satellite a question and hear my household's agent answer, so that the voice server I set up is useful.
2. As an owner, I want to add a voice agent from a template on the Agents page and give it a name, so that my household has something that answers aloud.
3. As an owner whose household has voice and no voice agent, I want the satellite to say so, so that I know what to add rather than waiting in silence.
4. As a member, I want my voice to be recognised as me by my household's voice server, so that my turn acts for me (my memory, my schedules) and not for the household guest.
5. As an owner, I want a timer set by voice to ring on the satellite I asked from, so that it works as the home's does.
6. As an owner, I want a reminder scheduled to deliver to `voice` to be spoken on my household's satellites, so that `deliverTo` means the same for every household.
7. As an owner, I want my Home Assistant watches to start a turn when they fire, so that home automations work outside the home.
8. As an owner, I want my library to tell me when a download finishes, so that I don't have to keep asking.
9. As an owner, I want a web login my agent hands off to wake its conversation when I finish it, so that logging in through my own web search works.
10. As an owner, I want a browse link my agent sends to open my household's web search, so that the page I see is the one my agent is reading.
11. As an owner, I want my channel servers to reconnect by themselves after a reboot, with no operator involved, so that a power cut at home costs me nothing.
12. As an owner, I want revoking a channel server's credential to disconnect it at once, so that a lost or stolen machine stops being heard.
13. As an owner, I want suspending my household to stop its channel servers too, so that suspension stops everything as it does today.
14. As an owner, I want my household's replies, alerts and approvals never to be spoken or shown on another household's devices, so that what my agent says stays in my house.
15. As an owner, I want a message from my channel server that claims to be from another household to be refused, so that a compromised server cannot act as someone else.
16. As the operator, I want the home's channels to behave exactly as now, so that this change cannot break the house it grew out of.
17. As the operator, I want the deployment to follow a household's channel servers from its tunnels alone, with nothing to configure per household, so that onboarding stays a credential and a kit.
18. As the operator, I want `redelegate` and `retire-signing-keys` to reach every household's channel servers that hold delegations, so that rotating keys does not silently break another household's watches.
19. As a developer, I want a connection to carry the household it serves, and every consumer to pick connections by household, so that the rule is checked in one place each, and tested.
20. As a developer, I want the voice template's prompt snapshot committed, so that a reviewer sees what a household's voice agent is told.

## Implementation Decisions

- **A channel connection belongs to a household.** A channel endpoint named by server name (not URL)
  is followed for every household. Each household's opening of that tunnel gets its own connection,
  which carries the household it serves. URL endpoints (the chat client, Telegram, the deployment's
  shared servers) stay one connection each, serving every household as now. The home is no longer
  special in following: `RelayedOwnServers.WatchAsync` yields openings with their household instead
  of filtering `IsHome`.
- **The set of connections is live.** Connections come and go as tunnels open and close, so the
  monitor reads a changing set instead of the list it was built with. Approvals, alerts, attachment
  fetches and delivery ask for the connections that serve a household. A closed tunnel's connection is
  "not connected" (ADR 0011) until its next opening, as now.
- **A message is believed only for its connection's household.** A turn whose verified household
  differs from the household of the connection it arrived on is refused and logged, before any agent
  is built. That covers a person's assertion, the guest's and a fire's delegation. The accounts
  back-call already mints a voice server's assertions for its tunnel's household alone; this is the
  agent's own check, so a server that forges or replays one is still refused.
- **Delivery picks the turn's household.** An interactive reply goes back on the connection the
  message came from (already so). A fan-out (`deliverTo` of a schedule, a watch, a login wake), the
  review host, the pre-minted topic of an attach-only target, and an attachment fetch pick, among the
  connections with that channel id, the deployment's one or the turn's household's one. They never
  take the first one of that id.
- **Approvals and alerts stay in the household.** An approval is offered, and an alert rings, only on
  the deployment's channels and the asker's household's own connections.
- **Each connection is told its household's agents.** Each connection is told its household's agent
  catalogue (`HouseholdAgentCatalogue`), not the home's.
- **A voice agent template.** `Domain/Prompts/Agents/nabu.md` is a template with the `voice` prompt
  section, the home Nabu's spoken-reply tuning without the home's servers (Home Assistant, music) and
  with the kit's own servers by name. Like the Jonas template, it reaches only the own servers the
  household set up. Its id is `nabu`, so the voice server's default `VoiceSettings.AgentId` addresses
  it unchanged. It is offered on the Agents page and not created for a new household by default.
- **A refused voice turn is spoken, not dropped.** When the agent refuses a turn from an attach-only
  channel, it replies on the turn with a short refusal: the agent is not one of the household's, the
  household is suspended, or the person is disabled. The voice server speaks it. A missing voice agent
  says to add one on the Agents page.
- **Topic minting checks the household.** `/internal/topics` refuses an assertion whose household is
  not the household of the tunnel the request came through.
- **Browse pages name their household.** A browse link carries the household whose web search serves
  it. The forwarder finds that household's tunnel (`TunnelRegistry.Find`) instead of `FindHome`.
- **Commands reach every household's channel servers.** `redelegate` and `retire-signing-keys` ask
  every household's open channel servers that hold delegations. Each household's servers are listed
  by name, and one that is not connected is reported as "could not be asked".

## Testing Decisions

- Good tests drive a household's channel server from the outside: a notification read through that
  household's tunnel, then the turn, the reply and any delivery observed where they land. They do not
  assert on connection bookkeeping.
- **Seam 1: the tunnelled channel round trip.** Prior art is `TunnelledChannelTests`, which today uses
  the home only. Cases:
  - A non-home household's channel notification is answered through its own tunnel.
  - Two households with a channel server of the same name each get their own replies.
  - A message asserting another household is refused.
  - A drop and reopen of a non-home tunnel is followed without a restart.
  - The home is unchanged.
- **Seam 2: the connection host.** Prior art is `ChannelConnectionHostTests`. Cases:
  - One run per opening per household.
  - Two households' openings of one server name run side by side.
  - A household's newer opening finishes only its own older run.
- **Seam 3: delivery and relays.** Prior art: `DeliveryTargetResolver` tests, `ApprovalRelayTests`,
  `AlertRelay` tests. Cases:
  - A fan-out to `voice` from a household's fire picks that household's voice.
  - An approval and an alert are offered only on the deployment's channels and the asker's household's.
  - An attachment is fetched from the connection that took it.
- **Seam 4: the voice template.** Covered by the template conformance tests, a prompt snapshot, and a
  `HouseholdAgents` unit test that a household's `nabu` made from it is offered to voice.
- **Seam 5: the smoke tier.** `scripts/smoke-stack.sh voice` gains a household case: a non-home
  household adds the voice template's agent, a second voice server and test satellite are started
  with that household's credential, and the spoken answer comes from that household's agent. A
  household with no voice agent hears the refusal.
- The eval's changed tier selects on prompt sections and skills, not on templates. Run the voice
  family on the new template's agent once its prompt is written.

## Out of Scope

- Telegram bots of a household's own: already configured per household (the keys page).
- A household's own Lemonade: voice back-calls the deployment's, billed as today.
- Music Assistant and the home's Home Assistant tuning in the voice template.
- Owners editing an agent's prompt sections or servers: still only name and instructions.
- Creating a voice agent automatically when a household mints `voice`.

## Further Notes

- `.claude/rules/tunnel.md` "A household's channel server" and the Jonas template's header comment
  are rewritten as each ticket lands.
- Deploy: the agent and the accounts service ship together; no compose or `.env` change. A
  household's kit voice needs its own satellites configured in its kit (`Satellites__*`), as the
  home's does.
