# A household's own channel servers answer it

Status: ready-for-agent

## Problem Statement

Some of a household's **own servers** are also **channel servers**: voice listens to the
satellites in the house, Home Assistant fires the household's **watches**, the library says when a
download has finished, and web search wakes a conversation when a person finishes a login it handed
off, and serves the pages a browse link opens. These are **dual-role servers**, and today the
deployment listens to each of them **only on the home household's tunnel**.

So an owner of any other household can **set up** voice: mint its credential on the Servers page and
start the **tenant kit**'s voice server at home. The **relay** admits the tunnel, the satellite hears
the question and turns it into text, and then nothing happens. Nobody reads that server's messages,
and the satellite waits two minutes in silence before giving up.

The same household's other dual-role servers fail the same way:
- its watches never start a turn;
- its library never says a download finished;
- a login handed off through its own web search never wakes the conversation;
- a browse link its agent sends opens the home's web search.

Nothing tells the owner why.

Even if the voice server were heard, its turn would be refused: voice addresses the agent `nabu`,
only the home has one, and no **agent template** makes a voice agent for anyone else.

Found in the manual test of 2026-11-02 on the smoke stack. The smoke script had minted the kit's
voice credential for a household other than the home by mistake, and the voice check timed out in
silence.

## Solution

The deployment listens to **every household's** channel servers. Each household's tunnel to a channel
server is a connection of that household's:
- A message arriving on it is believed only for that household.
- Everything sent back on it stays that household's: a reply, a scheduled delivery, an approval, an
  alert, an attachment fetch.

The deployment's own channels (the chat client and Telegram) keep serving every household, as now.

A **voice agent template** lets an owner add an agent that answers aloud, the way they add any agent
on the Agents page. The voice server addresses it as it addresses the home's Nabu today. A household
with voice and no voice agent hears a short spoken explanation instead of silence.

With that in place, every household's dual-role servers work as the home's do:
- its watches fire turns;
- its downloads announce themselves;
- its logins wake their conversation;
- its browse links open its own web search.

## User Stories

1. As an owner, I want to ask my satellite a question and hear my household's agent answer, so that the voice server I set up is useful.
2. As an owner, I want to add a voice agent from a template on the Agents page, so that my household has something that answers aloud.
3. As an owner, I want to name my voice agent, so that it answers to the name my household uses.
4. As an owner, I want to give my voice agent custom instructions, so that it answers the way my household likes.
5. As an owner whose household has voice and no voice agent, I want the satellite to tell me so, so that I know what to add instead of waiting in silence.
6. As an owner, I want the explanation to say where to add a voice agent, so that I can fix it without asking anyone.
7. As a member, I want my voice recognised as me by my household's voice server, so that my turn acts for me — my memory, my schedules — and not for the household's guest.
8. As a guest in the house, I want a question I ask aloud to be answered as the household's guest, so that visitors can use the satellites without an account.
9. As an owner, I want a timer I set by voice to ring on the satellite I asked from, so that it works as the home's does.
10. As an owner, I want a timer I set in chat to be able to ring on a satellite I name, so that I can start the pasta timer from my phone and hear it in the kitchen.
11. As an owner, I want a reminder scheduled to deliver to voice to be spoken on my household's satellites, so that a schedule's delivery means the same for every household.
12. As an owner, I want my voice agent to reach the own servers my household set up, and no others, so that it behaves like my other agents.
13. As an owner, I want my voice agent to be refused for the same reasons my other agents are (suspended household, disabled person), and told aloud, so that nobody is left waiting.
14. As an owner, I want my Home Assistant watches to start a turn when they fire, so that home automations work outside the home.
15. As an owner, I want a watch's effects delivered where the watch says, so that a warning set in chat arrives in chat and one set by voice is spoken.
16. As an owner, I want my library to tell me when a download finishes, so that I don't have to keep asking.
17. As an owner, I want a web login my agent hands off to wake its conversation when I finish it, so that logging in through my own web search works.
18. As an owner, I want a browse link my agent sends to open my household's web search, so that the page I see is the one my agent is reading.
19. As an owner, I want a browse link to show an offline page when my web search is not connected, so that I know to switch it on.
20. As an owner, I want my channel servers to reconnect by themselves after a reboot, with no operator involved, so that a power cut at home costs me nothing.
21. As an owner, I want a channel server that has not reconnected yet to cost me nothing more than its own silence, so that a switched-off satellite never breaks my chat.
22. As an owner, I want revoking a channel server's credential to disconnect it at once, so that a lost or stolen machine stops being heard.
23. As an owner, I want suspending my household to stop its channel servers too, so that suspension still stops everything.
24. As an owner, I want resuming my household to bring its channel servers back by themselves, so that a suspension leaves nothing to repair.
25. As an owner, I want my replies never spoken or shown on another household's devices, so that what my agent says stays in my house.
26. As an owner, I want my approvals and alerts never offered on another household's devices, so that nobody else can answer my questions or silence my timers.
27. As an owner, I want my attachments fetched only from where they were sent, so that a file never crosses into another household.
28. As an owner, I want a message from my channel server that claims to be from another household to be refused, so that a compromised server cannot act as someone else.
29. As an owner, I want each household's room names to be its own, so that two households can both have a "kitchen".
30. As a person in two conversations, I want a voice reply to go back to the satellite I spoke to, so that a question asked in the bedroom is not answered in the kitchen.
31. As the operator, I want the home's channels to behave exactly as now, so that this change cannot break the house it grew out of.
32. As the operator, I want a household's channel servers followed from its tunnels alone, with nothing to configure per household, so that onboarding stays a credential and a kit.
33. As the operator, I want rotating signing keys to reach every household's channel servers that hold delegations, so that rotating keys does not silently break another household's watches.
34. As the operator, I want a household's channel server that is not connected during a key rotation reported as not asked, so that I know whose watches need a second pass.
35. As the operator, I want each household's channel traffic counted under that household on the dashboard, so that one household's voice does not look like the home's.
36. As the operator, I want a message refused for claiming the wrong household logged with both households, so that I can see a misbehaving server.
37. As a developer, I want a connection to know the household it serves, and every consumer to choose connections by household, so that the rule is checked in one place each.
38. As a developer, I want the voice template's prompt snapshot committed, so that a reviewer sees what a household's voice agent is told.
39. As a developer, I want the glossary to say what a channel connection of a household is, so that the code and the prose agree.

## Implementation Decisions

- **A channel connection belongs to a household.** A channel server named by its server name (as
  the home's dual-role servers are configured today) is followed for every household. Each
  household's tunnel for it is a connection of that household's. A channel dialled by URL (the chat
  client, Telegram) stays one connection serving every household.
- **The home is no longer special in following.** The relay's watch over tunnels yields each opening
  with its household instead of filtering to the home's. One opening is still one connection
  generation, and its tool set is fixed for that generation (ADR 0012).
- **The set of connections is live.** Connections appear and disappear as tunnels open and close, so
  the channel monitor reads a changing set instead of the list it started with. A connection whose
  tunnel closed is not connected until its next opening, with the five behaviours ADR 0011 keeps.
  Since a household's channel server is dynamic, it is dropped while away and never fails the agent
  (ADR 0027).
- **A message is believed only for its connection's household.** A turn whose verified household
  differs from the household of the connection it arrived on is refused and logged, before any agent
  is built. This applies to a person's assertion, the guest's and a fire's delegation. The accounts
  back-call already mints a voice server's assertions only for its tunnel's household; this check is
  the agent's own, so a forged or replayed assertion is still refused.
- **Choosing a connection means choosing by household.** For a channel id, the candidates are the
  deployment's own connection and the turn's household's connection. Nothing takes "the first
  connection with that id". This covers:
  - an interactive reply (already sent back on the connection the message came from);
  - a fan-out's delivery (a schedule's, a watch's, a login wake's);
  - the review copy's host;
  - the topic created ahead of a turn for an attach-only target;
  - an attachment fetch;
  - the approvals relay;
  - the alerts relay.
- **Each connection is told its household's agents.** Each connection registers its own household's
  agent catalogue, not the home's. The glossary entry for **household agent catalogue** changes to
  say so.
- **Topic creation checks the household.** The agent's internal topic-minting route refuses an
  assertion whose household is not the household of the tunnel the request came through.
- **A voice agent template.** A third agent template beside Jonas and Sugary:
  - It carries the voice prompt section and the home Nabu's spoken-reply tuning, without the home's
    Home Assistant and music.
  - It names the kit's own servers by name, and reaches only those the household set up.
  - Its id is `nabu`, so a household's voice server addresses it with no setting of its own.
  - Owners add it on the Agents page. It is not created for a new household by default.
- **A refused turn on an attach-only channel gets a reply.** When the agent refuses a turn from a
  channel that only attaches (voice), it replies with a short refusal for the server to speak,
  instead of nothing. A missing voice agent says to add one on the Agents page. A suspended household
  and a disabled person say what they say elsewhere.
- **Browse pages name their household.** A browse link carries the household whose web search serves
  it. The relay forwards it to that household's tunnel, not the home's.
- **Key rotation reaches every household.** Redelegating and retiring signing keys ask every
  household's open channel servers that hold delegations. Each is listed by household and server
  name, and one that is not connected is reported as not asked.
- **Satellites and speaker profiles stay in the kit.** Both are already per household and need
  nothing new. The timers server already rings a household's satellites through that household's
  voice server.

## Testing Decisions

- A good test drives a household's channel server from the outside and asserts where things land:
  - the server raises a notification through the household's tunnel;
  - the test observes the turn's agent, the reply on the same tunnel, a fan-out's delivery, an
    approval's or an alert's offers, or a refusal.

  It never asserts on how connections are kept.
- **Seam 1 is the main one: the tunnelled channel round trip.** An agent host with the real relay and
  in-process own channel servers, one or more per household. Prior art is the tunnelled channel
  tests, the browse page tests and the redelegation-over-tunnel tests, which today use the home
  only. Cases:
  - Following and replies:
    - a non-home household's notification is answered by its own agent through its own tunnel;
    - two households with a channel server of the same name each get only their own replies;
    - a message asserting another household is refused, and no agent is built;
    - a non-home tunnel's drop and reopen is followed without a restart;
    - the home is unchanged.
  - Delivery, approvals, alerts:
    - a household's fire delivering to voice reaches its own voice server;
    - an approval and an alert are offered only on the deployment's channels and the asker's
      household's;
    - an attachment comes from the connection that took it.
  - Refusals: a voice turn for an agent the household lacks gets the spoken refusal; so do a
    suspended household and a disabled person.
  - The other dual-role servers: a non-home household's Home Assistant watch, library download
    notice and finished web login each start that household's turn; its browse link opens its own
    web search, or the offline page; redelegation reaches its Home Assistant server and reports an
    offline one as not asked.
- **Seam 2: the smoke tier.** Prior art is the smoke voice check and the smoke test that onboards a
  household end to end. A non-home household adds the voice template's agent, a second kit voice
  server and test satellite start with that household's credential, and the spoken answer comes from
  that household's agent. A timer set by voice rings on its satellite. A household with voice and no
  voice agent hears the refusal. The home's voice check runs beside it, unchanged.
- **Template:** the voice template is held by the existing template conformance tests and a
  committed prompt snapshot, not a new seam. The eval's changed tier selects on prompt sections and
  skills, not on templates, so run the voice family on the template's agent once its prompt is
  written.

## Out of Scope

- A household's own Telegram bots: already configured per household on the Keys page.
- A household's own Lemonade: voice keeps calling back to the deployment's, as now.
- Music Assistant and the home's Home Assistant tuning in the voice template.
- Owners editing an agent's prompt sections or servers: still only its name and instructions.
- Creating a voice agent automatically when a household sets up voice.
- Showing an owner on the Servers page which of their channel servers no agent answers.

## Further Notes

- The tunnel rules' section on a household's channel server, the Jonas template's header comment and
  the glossary's **household agent catalogue** entry are rewritten as the work lands.
- Deploy: the agent and the accounts service ship together. There is no compose or `.env` change. A
  household's kit voice needs its own satellites configured in its kit, as the home's does.
- A household's satellites carry room names of their own. Two households both naming a room
  "kitchen" is fine, because satellites are only ever addressed through their own household's voice
  server.
