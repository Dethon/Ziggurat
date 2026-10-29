# Accounts

Status: ready-for-agent

## Problem Statement

Nobody signs in to Ziggurat. The chat client lets the browser pick a name from a list the server
publishes to anyone, and that name is believed all the way down to a person's remembered web
logins. Telegram admits whoever's username, or first name, matches a list, and a username can be
taken by someone else later. A voice turn is the satellite's fixed `household` unless a speaker is
enrolled. Sugary is one log behind an access code that the deployment switches off. The dashboard,
which shows memory text and usage per person, has no sign-in at all.

The same person is also several people. Memory is filed under whatever sender each channel reports,
so what someone tells the agent on Telegram is unknown to it on WebChat, and every unrecognized
voice turn writes into one shared bucket. The only thing that ties a person's channel names
together is an identity map kept in environment variables, and only web browsing reads it.

Every topic in a space is visible to everyone in it, an approval can be answered by anyone who sees
it, and there is no way to invite someone, take a device away, or get back in after losing one.

## Solution

Every person has an account in their household, and signs in to the web with a passkey. A new
accounts service owns households, people, roles, passkeys, invites and sessions. The reverse proxy
asks it about every web request and hands the app the verified person and household, so the chat
client, its hub, Sugary, SexyTime and the dashboard believe only that.

A person is one account with linked identities: their passkeys, their numeric Telegram id and their
voice profile. Each channel server resolves the identity it saw to the account and stamps the
turn's principal with it. Memory, remembered web logins, push subscriptions, approvals, schedules
and the Sugary log all follow the account, whichever channel a turn came in on. The identity map
and its environment variables retire.

An owner invites people with a single-use link. The invited person redeems it either by enrolling a
passkey or by opening it in Telegram, so someone who only ever uses Telegram still has a full
account and can add a passkey later. An owner can recover a member who lost every device; the
operator has a command for their own recovery.

Topics are private to the person whose they are, including the review copies that voice, Sugary
and, from now on, Telegram ask for. A person can share a topic with the household. An owner can
grant specific members permission to see every topic in the household.

Today's people, memories, logins, voice profiles, schedules, push subscriptions and Sugary log move
into the home household in one migration, so nothing is lost and nobody starts over.

## User Stories

### Signing in and devices

1. As the operator, I want a command run inside the accounts service that prints a one-time enrollment link, so that I can enroll my first passkey before any account can sign in.
2. As the operator, I want that same command to work when I have lost every passkey, so that I can never be locked out of my own deployment.
3. As a person, I want to sign in by choosing one of my passkeys, with no username to type, so that signing in is one touch.
4. As a person, I want my sign-in to last until I revoke it, so that I am never asked again on my own devices.
5. As a person, I want one sign-in to cover the chat client, Sugary and SexyTime, so that I don't sign in once per app.
6. As a person, I want to add another passkey from my account page, so that my phone and my laptop both work.
7. As a person, I want to see every passkey I hold and every device I am signed in on, so that I know where my account is open.
8. As a person, I want to revoke a signed-in device, so that a lost or sold phone stops working.
9. As a person, I want revoking a passkey to end every session it began, so that removing a compromised key closes what it opened.
10. As a person who is signed out, I want any protected page to take me to sign-in and back, so that a bookmark still works.
11. As a person using an app's API from the page, I want a signed-out call to fail plainly rather than redirect, so that the page can send me to sign-in.

### Invites and recovery

12. As an owner, I want to invite someone with a single-use link that names their display name and role, so that only the people I choose get in, as what I choose.
13. As an owner, I want an invite to expire after a week, so that a forgotten link stops working on its own.
14. As an invited person, I want to redeem my invite by enrolling a passkey in my browser, so that I can use the web apps.
15. As an invited person who only uses Telegram, I want to redeem my invite by opening it in Telegram, so that I never need a browser to have an account.
16. As a person who joined through Telegram, I want to ask the bot for a link that adds a passkey, so that I can start using the web whenever I want.
17. As an owner, I want to issue a recovery link for a member who lost every passkey, so that they can enroll a new one without a new account.
18. As an owner, I want to change a member's role, so that I can share running the household.
19. As a member, I want the members page to be closed to me, so that only owners manage the household.

### Linking channels

20. As a person, I want my account page to give me a code that links my Telegram, so that the bot knows me by my numeric id.
21. As a person, I want my account page to show the command that enrolls my voice, so that the satellites can recognize me.
22. As a person, I want the bot to recognize me by my numeric Telegram id, so that someone who takes my old username is not me.
23. As someone who is not linked, I want the bot to tell me to ask an owner for an invite, so that I know how to get in.
24. As an owner, I want to bind a Telegram group to my household with a code, so that its members' messages are their own turns.
25. As a person in a bound group, I want my messages there to be my turns, with my memory and my logins, so that the group is as useful as a private chat.
26. As a person, I want nabu to recognize my voice as my account, so that it acts as me.
27. As a household, I want a voice nabu doesn't recognize to act as a guest that is nobody, so that a visitor or the television can't reach anyone's memory, logins or Sugary log.
28. As a guest, I want to still control the house, set timers and ask questions, so that the house works for everyone in it.

### The chat client

29. As a person, I want the chat client to know me from my sign-in, with no user picker, so that nobody can pick my name.
30. As a person, I want my name and avatar in the chat client to come from my account, so that I set them once.
31. As a person, I want my topics private by default, so that what I ask stays mine.
32. As a person, I want to share a topic with the household, so that we can all read and continue it in its space.
33. As a person, I want to stop sharing a topic I shared, so that I can take it back.
34. As a member reading a shared topic, I want to continue it, with my message counted as my own turn, so that we can work on it together.
35. As a person, I want another person's private topic to be absent from my lists and my searches, and refused if I name it, so that privacy doesn't depend on not knowing an id.
36. As an owner, I want to grant "sees all conversations" to specific members, so that a parent can follow the household's topics.
37. As a person, I want to see who in my household holds "sees all conversations", so that nobody is watched without knowing it can happen.
38. As a member with "sees all conversations", I want every topic in the household, private ones and review copies included, so that the permission means what it says.
39. As a person on Telegram, I want my private chat with the bot to show as a review copy among my own topics, so that all my conversations are in one place.
40. As a household, I want a Telegram group's review copy to be a shared topic, so that everyone sees what the group asked.
41. As a household, I want a voice guest's review copy to be a shared topic, so that we all see what was asked of the house.
42. As a person, I want my voice and Sugary review copies to be mine, private like my other topics.
43. As a person, I want my push notifications to follow my account, so that a notification meant for me reaches my devices and nobody else's.

### Memory, logins and schedules

44. As a person, I want one memory across Telegram, the chat client and voice, so that what I told the agent on one is known on the others.
45. As a guest, I want nothing remembered from my turn, so that a visitor's words don't end up in anyone's memory.
46. As a person, I want the agent to browse as me with my remembered logins from any of my channels, and never as anyone else, so that my accounts stay mine.
47. As a person on voice, I want a login handoff link delivered to my own chat client again, so that a voice request can reach a login.
48. As a person who started a login handoff on Telegram, I want the handoff link to open without a passkey session, so that I can finish it from my phone.
49. As the creator of a schedule, I want its fires to run as me and its approvals to come to me, so that a schedule acts for whoever wrote it.
50. As a SexyTime user, I want my custom agents to belong to my account, so that nobody else sees or uses them.

### Approvals

51. As a person, I want only my own answer to settle an approval my turn asked for, so that nobody approves for me.
52. As a person, I want the approval offered on my other channels too, with the first answer winning, so that I can tap on Telegram when nabu asked out loud.
53. As a person, I want an approval I answered elsewhere to show as answered elsewhere, so that I am not asked twice.
54. As a member with "sees all conversations", I want to see an approval in someone's topic without being able to answer it, so that reading never means deciding.

### Sugary

55. As a person, I want my own Sugary log of meals, doses, pens, cycle, day notes and exercise, so that my health data is mine.
56. As a household, I want foods, recipes and the pantry shared, so that a recipe cooked for everyone is entered once.
57. As a person, I want Sugary to know me from my sign-in, with no access code, so that it is both safer and easier.
58. As a person asking Jonas about my carbs, I want it to read my Sugary log and nobody else's, so that asking can't reveal another person's data.
59. As a person, I want my Sugary pen reminders to reach my devices only.

### Operator

60. As the operator, I want the dashboard reachable by me alone, so that memory text and usage per person stay private.
61. As the operator, I want the ServiceBus channel gone, so that no channel lets a message name its own sender.
62. As the operator, I want one migration that carries today's people, memories, logins, voice profiles, schedules, push subscriptions and Sugary log into the home household, so that nothing is lost.
63. As the operator, I want the migration to be safe to run twice, so that a failed run can simply be repeated.
64. As the operator, I want the migration to print each person's passkey link, so that I can hand them out.
65. As a person after the migration, I want my memories from every channel merged into one, so that the agent knows me as one person from the first day.

## Implementation Decisions

### The accounts service

- A new host and container, the accounts service, on ASP.NET Core with WebAuthn through Fido2NetLib.
  It is the only owner of households (tenants), people (users), roles, passkey credentials, linked
  identities, invites, recovery links and sessions.
- A user has a display name, an avatar, a role (`owner` or `member`), an operator flag and a
  `SeesAllConversations` permission. A user belongs to one tenant. Tenant and user ids are opaque
  and generated; a name is never a key.
- Linked identities are a user's passkey credentials, at most one numeric Telegram id and at most
  one voice profile.
- Storage is EF Core on SQLite in the service's own volume, migrated at start, as Sugary's log is
  (ADR 0043). Nothing in the model or migrations is SQLite-specific, so a later move to a hosted
  relational database is a provider change.
- Only the home tenant exists in this phase. Every record carries its tenant anyway.

### Passkeys, sessions, invites and recovery

- Passkeys are discoverable credentials: sign-in asks for no username. A person may hold several.
- A session is an opaque random id kept in the store and carried in an HttpOnly, Secure,
  SameSite=Lax cookie on the deployment's parent domain, valid until revoked. A session records
  the passkey that began it; revoking the passkey revokes its sessions.
- Invites and recovery links are single-use and expire after seven days. An invite names a role and
  a display name. It can be redeemed by enrolling a passkey or through Telegram's deep-link start
  parameter. A recovery link enrolls a new passkey for an existing user.
- Owners mint invites and recovery links from the members page. The operator's command, run inside
  the accounts container, mints an enrollment or recovery link for any user.

### The session check at the proxy

- The reverse proxy sends every request on a protected route to the accounts service's session
  check. A valid session answers success with three headers: `X-Ziggurat-User`,
  `X-Ziggurat-Tenant` and `X-Ziggurat-Roles`. The proxy copies them onto the upstream request,
  overwriting any the client sent.
- With no session, a page request is redirected to sign-in and returned to afterwards, and an API
  or hub call is answered 401.
- Protected: the chat client, its hub, its attachment and dictation endpoints, the agent
  registration API, SexyTime, Sugary and the dashboard. The dashboard admits only the operator.
- Not protected: the login handoff's live view and the logins page, which keep their link-claimed
  cookies so a Telegram or voice person can open them; and the accounts service's own sign-in and
  enrollment pages.
- Apps trust the headers because the security baseline makes them reachable only through the proxy.
  Signing what they carry is the Service identity phase's job.

### The chat client and its hub

- The client loses its user list, its picker and its stored name. Registering a hub connection
  takes no argument: the hub reads the user from the forwarded headers. Display name and avatar
  come from the account.
- Every hub call acts only on topics and approvals the connection's user may see.
- Push subscriptions are keyed by user id.

### Topics

- A topic records its owner, the person whose topic it is, and a `Shared` flag only the owner can
  set or clear.
- A person sees their own topics, shared topics in their household's spaces, and, with
  `SeesAllConversations`, every topic in the household.
- Anyone who can see a shared topic can continue it, and their message is their own turn.
- A review copy's ownership comes from the turn's principal, decided by the review host: a person's
  turn gives a topic that person owns; a principal with no user, a voice guest's, gives a shared
  topic; a Telegram group's is shared. The channel still only asks for a review copy and never
  names where it goes (ADR 0046).
- A member with `SeesAllConversations` sees approvals in others' topics but cannot answer them.

### Channels and the principal

- `Principal` becomes the turn's tenant id, user id and channel id. The user id is absent for a
  voice guest. `Sender` stays a display name and is never a key.
- Channel servers resolve the identity they saw through the accounts service's internal resolution
  API, called with the channel server's own credential from the baseline, and put the principal on
  the channel notification.
- **Telegram.**
  - A start parameter redeems an invite (creating the account) or a link code from the account
    page, recording the numeric id.
  - A web-link command sends a linked person a one-time link that adds a passkey.
  - A bind command with an owner's code binds a group to the household.
  - A message from an unlinked person, private or in a group, is refused with a hint to ask an
    owner for an invite. Linked identities replace the baseline's numeric allowlist.
  - Telegram asks for a review copy on every turn.
- **Voice.**
  - A speaker profile is named after its user id, and a conclusive match is that user.
  - No match is the voice guest: a principal with no user, no memory extraction, and no person's
    logins or Sugary log. The guest can still control the house, set timers and ask questions.
- **Retirements.** The identity map, its configuration (people and verified channels) and its
  environment variables go. The `asMe` gate, the login handoff and the logins page read the
  principal's user id. The chat client counts as a verified channel, so voice login, which hands
  its link to the person's chat client, works again.
- **ServiceBus.** The ServiceBus channel server is removed, along with its deployment entry, its
  channel endpoint and its tests.

### Keyed by user id

- **Memory.** Memory entries and the personality profile are keyed by the principal's user id;
  recall and extraction read it, never `Sender`. A turn with no user recalls and extracts nothing.
- **Web logins.** A remembered login record is sealed with the user id as associated data.
- **Schedules.** A schedule's `CreatedBy` is the principal of the turn that last wrote it, now
  carrying the user id.
- **Custom agents.** Keyed by user id; the agent registration API takes the user from the
  forwarded headers and ignores any user parameter. They stay in memory; making them durable
  belongs to the Tenants phase. The agent-API secret stays for SexyTime's server.

### Approvals

- An approval records the asking principal, and only that user's answer settles it.
- It is shown where the turn asked, and also offered in the person's Telegram private chat and as
  a chat-client notification. The first answer wins; the others show "answered elsewhere".
- A fire's approval goes to the schedule's creator.

### Sugary

- Meals, doses, pens, periods, day notes, exercise sessions and push subscriptions gain a user id.
- Foods, recipes and the pantry gain a tenant id and stay shared by the household.
- The access code and its settings go. The app and its API act for the forwarded user, and so do
  its channel turns.
- Jonas's Sugary mount and its query tool answer for the turn's person only.

### Migration

- One idempotent command creates the home tenant. It takes the operator, the owner and the person
  whose Sugary log it is as arguments.
- From the identity map, it creates one user per person with their Telegram id, voice profile and
  chat-client name linked, and prints each a one-time passkey link.
- It moves data onto the new user ids:
  - Merges each person's memories from every channel name into their user id, and the `household`
    bucket into the owner's.
  - Re-seals remembered logins under the user id.
  - Renames voice profiles to user ids and re-keys push subscriptions.
  - Re-stamps schedules' `CreatedBy`, to the owner where the old principal names nobody.
  - Gives today's Sugary rows to the named person.
- It marks every existing topic shared, since none records an owner; people can make their own
  private afterwards.

## Testing Decisions

A good test drives a module through its outer surface and asserts what a person or a caller
observes: what a page shows, what a request is answered, what a tool returns for a given
principal, what a channel server sends the agent. It never asserts which internal method ran or
how a record is laid out.

Six seams, two of them new.

1. **The accounts service's HTTP surface (new).** A test host for the service, and Playwright with
   Chromium's virtual authenticator for the WebAuthn ceremonies.
   - Covers: invite minting, redemption, expiry and single use; recovery; enrollment and sign-in
     with no username; adding and revoking passkeys and devices; revoking a passkey ending its
     sessions; the session check's headers, redirect and 401; the members page refusing a member;
     the internal resolution API.
   - Prior art: HTTP endpoints driven on a test host (`WatchFiredEndpointTests`,
     `WebLoginHandoffTests`); the Playwright fixtures under the E2E suite.
2. **The signed-in chat-client E2E stack (existing).** The stack signs in through the accounts
   service with a virtual authenticator, and the existing chat-client E2E suites run signed in.
   - Covers: the proxy's checks; hub authorization against another person's topics; owner, shared
     and sees-all visibility; review-copy ownership; an approval answered elsewhere.
   - Prior art: `WebChatStack` and the chat-client E2E suites.
3. **Channel servers with fake transports (existing).** Each asserts the principal on the
   notification the channel server sends.
   - Covers: Telegram invite redemption through the start parameter, the web-link and bind
     commands, an unlinked sender refused, a group member's turn; a matched speaker's turn and a
     guest's.
   - Prior art: `TelegramBotServiceTests` with a fake bot client; `TranscriptDispatcherTests`;
     `ChannelReceiveContractTests`.
4. **MCP tool calls with a principal in `_meta` (existing).**
   - Covers: memory recalled for one user across two channels and not for another, and a guest
     extracting nothing; a remembered login reachable by its owner only; a schedule recording its
     writer's user id; Sugary rows invisible across people while recipes are shared.
   - Prior art: `McpWebSearchSessionScopeTests`; `ScheduleFileSystemJourneyTests`; the Sugary API
     integration tests (`SugaryPeriodApiTests`).
5. **The migration command (new).** Run against a seeded Redis and a seeded Sugary database.
   - Covers: users created and linked; memories merged; logins re-sealed and readable; schedules
     re-stamped; topics shared. A second run changes nothing.
   - Prior art: the integration fixtures that start Redis and Sugary as testcontainers.
6. **Configuration read as files (existing).** The Caddyfile and compose files read as text.
   - Covers: every protected route goes through the session check and copies its headers;
     `/browse/*` doesn't; the accounts service follows the baseline's per-service environment
     rules.
   - Prior art: `VfsBridgeApiTests.ThePublicProxy_DoesNotRouteTheBridge`.

The eval harness stamps principals with user ids so its scenarios keep running; no scenario's
claims change in this phase.

## Out of Scope

- More than one household, tenant configuration, tenant-brought keys, and owners managing agents
  and features: the Tenants phase.
- Signed identity between services: the Service identity phase. Here a server believes the
  principal from a caller that holds its credential, and an app believes the proxy's headers.
- Enrolling a voice profile from the browser.
- Durable custom agents.
- Usage views for owners; the dashboard stays the operator's.
- Encrypting memories, topics or the Sugary log at rest.

## Further Notes

- This is phase 2 of the multi-tenant plan (`.scratch/multi-tenant/plan.md`). It branches from
  `web-identity` after the security baseline and merges back into it. Master gets every phase
  together.
- Deploying needs the operator to:
  - run the migration once, naming themselves as operator and owner and naming the person whose
    Sugary log it is;
  - send each person their passkey link;
  - set the accounts service's secrets and the proxy's session-check routes.
