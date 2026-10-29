# Accounts

Status: ready-for-agent

Phase 2 of `.scratch/multi-tenant/plan.md`. Branches from `web-identity` (after the security
baseline), merges back into it. Only the home household exists in this phase, but every record
already carries its tenant.

## Problem Statement

Nobody signs in. WebChat lets the browser pick a name from a published list and believes it all the
way down to a person's saved web logins. Telegram is matched by a username anyone can later take.
Voice is a satellite's fixed `"household"` unless a speaker is enrolled. Sugary is one shared log
behind an access code that compose switches off. The dashboard, which shows memory text and usage
per person, has no sign-in at all.

The same person is also several people. Memory is keyed by the raw sender each channel gives, so
Telegram's `fran` and WebChat's `Fran` remember different things, and every unrecognized voice turn
writes into one shared `household` bucket. The identity map that ties a person's channel names
together lives in compose environment variables and serves only web browsing.

Every topic in a WebChat space is visible to everyone, approvals can be answered by whoever sees
them, and there is no way to invite someone, remove a device, or recover a lost one.

## Solution

A new **accounts service** owns households (tenants), people, roles, passkeys, invites and
sessions. People sign in with a passkey; Caddy asks the accounts service about every web request
(`forward_auth`) and hands the app the verified person and household. WebChat, its hub, Sugary,
SexyTime and the dashboard believe only that.

A person is one account with linked identities: passkeys, a numeric Telegram id, a voice profile.
Every channel resolves the identity it saw to the account, and `Principal` carries the account's
user and tenant ids, so memory, web logins, push subscriptions, approvals and the Sugary log follow
the person across channels. The identity map and its environment variables retire.

The owner invites people. An invite is redeemed by enrolling a passkey or by opening a Telegram
deep link, so someone who only ever uses Telegram has a full account. An owner can recover a member
who lost every device, and the operator has a command for their own recovery.

Conversations are private to the person they belong to, including the review copies voice, Sugary
and now Telegram mirror into WebChat. A person can share a topic with the household. An owner can
grant specific members permission to see every conversation.

Today's people, memories, logins, voice profiles, schedules and Sugary log move into the home
household in one migration.

## User Stories

1. As the operator, I want a one-time link from a command inside the accounts container, so that I can enroll my first passkey and recover myself if I lose every device.
2. As an owner, I want to invite someone by a single-use link that names their role and display name, so that only the people I choose get in.
3. As an invited person, I want to redeem the invite by enrolling a passkey in my browser, so that I can use WebChat, Sugary and SexyTime.
4. As an invited person who only uses Telegram, I want to redeem the invite by opening a Telegram link, so that I never need a browser to have an account.
5. As a Telegram-only person, I want the bot to send me a link to add a passkey when I ask, so that I can start using the web later.
6. As a person, I want to sign in by choosing my passkey, with no username to type, so that signing in is one touch.
7. As a person, I want to add more passkeys and see every device I'm signed in on, so that I can use my phone and my laptop.
8. As a person, I want to revoke a device or a passkey, so that a lost phone stops working; revoking a passkey ends its sessions.
9. As a person, I want my sign-in to last until I revoke it, so that I'm not asked again on my own devices.
10. As an owner, I want to issue a recovery link for a member who lost every passkey, so that they can enroll a new one.
11. As a person, I want WebChat to know me from my sign-in, with no user picker, so that nobody can pick my name.
12. As a person, I want my topics private by default, so that what I ask stays mine.
13. As a person, I want to share a topic with the household, so that we can all see and continue it in its space.
14. As an owner, I want to grant "sees all conversations" to specific members, so that a parent can see the household's conversations; everyone can see who holds it.
15. As a member with "sees all conversations", I want to read every member's topics and review copies, including Telegram and voice conversations, so that the permission means what it says.
16. As a person on Telegram, I want my private chat with the bot mirrored into my WebChat as a private review copy, so that my conversations are in one place.
17. As a household, I want a guest's voice conversation to show as a shared topic, so that we all see what was asked of the house.
18. As a person, I want one memory across Telegram, WebChat and voice, so that what I told the agent on one is known on the others.
19. As a person, I want my web logins, push subscriptions and Sugary log tied to my account, not to a name a channel happens to report.
20. As a person, I want only my own answer to settle an approval my turn asked for, from any of my channels, first answer winning, so that nobody approves for me and I can tap when voice asked.
21. As the creator of a schedule, I want its approvals to come to me.
22. As a person enrolled on voice, I want my voice to resolve to my account, so that nabu acts as me; as an unrecognized voice, a guest acts as nobody and nothing is remembered from it.
23. As a person in a Telegram group bound to my household, I want my messages to be my turns, and an unlinked stranger refused with a hint to ask an owner for an invite.
24. As a person, I want my own Sugary log of meals, doses, pens, cycle and exercise, with foods, recipes and the pantry shared by the household.
25. As the operator, I want the dashboard reachable only by me.
26. As a SexyTime user, I want SexyTime behind the same sign-in, and my custom agents to belong to my account.
27. As a person who started a login handoff on Telegram or voice, I want the handoff link to work without a passkey session, as today.
28. As the operator, I want today's people, memories, logins, voice profiles, schedules and Sugary log carried into the home household, so that nothing is lost and nobody starts over.

## Implementation Decisions

- **The accounts service** is a new host and container (`Accounts/`, container `accounts`), ASP.NET
  Core, with WebAuthn through Fido2NetLib. It owns tenants, users (display name, avatar, role
  `owner` or `member`, an operator flag, a `SeesAllConversations` permission), passkey credentials,
  linked identities (Telegram numeric id, voice profile), invites, recovery links and sessions.
  Ids are opaque and generated; a name is never a key.
- **Storage** is EF Core on SQLite in the accounts volume, migrations run at start (prior art:
  Sugary). Nothing SQLite-specific in the model or migrations, so moving to a hosted relational
  database is a provider change.
- **Passkeys** are discoverable credentials: sign-in has no username field. A person may hold
  several. Sessions are opaque random ids in the store, carried in an HttpOnly, Secure,
  SameSite=Lax cookie on the parent domain, valid until revoked. Revoking a passkey revokes the
  sessions it began.
- **Invites and recovery** are single-use links that expire after seven days, minted by an owner
  (or by the operator command for the operator). An invite names role and display name and can be
  redeemed by enrolling a passkey or through a Telegram deep link (`t.me/<bot>?start=<code>`). A
  recovery link enrolls a new passkey for an existing person. The operator command runs inside the
  container (`docker exec accounts accounts mint-link …`).
- **forward_auth.** Caddy sends each request on the protected hosts to the accounts service's
  verify endpoint. A valid session answers 2xx with `X-Ziggurat-User`, `X-Ziggurat-Tenant` and
  `X-Ziggurat-Roles`, which Caddy copies onto the upstream request, overwriting anything the client
  sent. No session sends a browser to sign-in and a hub or API call a 401. Protected: WebChat, its
  hub, attachments and dictation, `/api/agents`, `sexy.*`, `sugary.*`, the dashboard (operator
  only). Not protected: `/browse/*` and the logins page, which keep their link-claimed cookies, and
  the accounts service's own sign-in and enrollment pages. Apps trust the headers because the
  baseline makes them reachable only through Caddy; signing what they carry is Service identity's
  job.
- **WebChat** drops the user list and the client-asserted name. `RegisterUser` takes no argument:
  the hub reads the user from the forwarded headers. Every hub method acts only on topics and
  approvals the connection's user may see. Push subscriptions are keyed by user id. Avatars and
  display names come from the account.
- **Topics.** A topic has an owner (the person whose conversation it is) and a `Shared` flag only
  its owner sets. A person sees their own topics, shared topics in their household's spaces, and,
  with `SeesAllConversations`, every topic of the household. A review copy (voice, Sugary,
  Telegram) belongs to the turn's person; a voice guest's and a Telegram group's are shared.
  Anyone who can see a shared topic can continue it, and their message is their own turn.
- **Telegram** asks for a review copy on every turn, private chats and groups alike (prior art:
  Sugary's `ReviewCopy`). Linking happens through the deep link: `/start <code>` redeems an invite
  or a link-Telegram code from the account page. `/web` sends a linked person a one-time link to
  add a passkey. The baseline's `AllowedUserIds` gives way to linked identities. A group is bound
  to a household when an owner runs `/bind <code>` in it; messages from an unlinked person in it
  are refused with a hint. The sender resolves through the accounts service.
- **Voice.** A speaker profile folder is named after the user id; the account page shows the
  enrollment command. A conclusive match is that user. No match is the household guest: no
  person's memories, logins or Sugary, no memory extraction, and a shared review copy. It can still
  control the house, set timers and answer questions.
- **Principal.** Channel servers resolve the identity they saw to an account through the accounts
  service's internal API (their own credential, per the baseline) and put `Principal(TenantId,
  UserId, ChannelId)` on the notification; `Sender` stays a display name. The voice guest has a
  principal with no user. `IdentityMap`, `Identity:People`, `Identity:VerifiedChannels` and
  `IDENTITY__PEOPLE__*` go; `AsMeGate` and the login handoff read the principal's user id, and
  `signalr` counts as verified, so voice login is back on.
- **Keyed by user id:** memory (`memory:{userId}:…`, profile, recall and extraction from the
  principal, never `Sender`), web login records (sealed with the user id as associated data), push
  subscriptions, schedules' `CreatedBy`, custom agents (still in memory; durable in Tenants), and
  approvals.
- **Approvals** record the asking principal. Only that user's answer counts. The approval is shown
  where the turn asked and is also offered in the person's Telegram private chat and as a WebChat
  notification; the first answer wins and the others show "answered elsewhere". A fire's approval
  goes to the schedule's creator.
- **Sugary.** Meals, doses, pens, periods, day notes, exercise sessions and push subscriptions get a
  user id; foods, recipes and the pantry get the tenant id and stay shared. The access code goes;
  the app and its API sit behind `forward_auth` and act for the forwarded user, and so do its
  channel turns. Jonas's `/sugary` mount answers for the turn's person only.
- **SexyTime** sits behind the same sign-in. `/api/agents` takes the user from the forwarded
  headers; the `userId` query parameter goes. The agent-API secret stays for its server's calls.
- **ServiceBus** is removed: the `McpChannelServiceBus` project, its compose service, its channel
  endpoint and its tests.
- **The dashboard** sits behind `forward_auth` and admits the operator only.
- **Migration** is one idempotent command run once against the stack. It creates the home tenant
  and, from the identity map, one user per person with their Telegram id, voice folder and WebChat
  name linked, and prints each a one-time passkey link. It names the operator and the owner from
  its arguments. It merges each person's memories from every channel name into their user id and
  `household`'s into the owner's, re-seals web logins under the user id, renames voice folders,
  re-keys push subscriptions, re-stamps schedules' `CreatedBy` (to the owner where the old
  principal names nobody), gives today's Sugary rows to the person named in its arguments, and
  marks every existing WebChat topic shared, since none has an owner on record.

## Testing Decisions

Test through each module's outer surface: what a page shows, what a request is answered, what a
tool returns for a given principal. Never which internal method ran.

- **The accounts service over HTTP** with a test host: invite minting, redemption, expiry and
  single use; recovery; session issue, verify and revoke; revoking a passkey ending its sessions;
  the verify endpoint's headers and its 401 versus redirect.
- **Passkey ceremonies in a real browser** through Playwright's virtual authenticator (CDP
  `WebAuthn.addVirtualAuthenticator`): enroll from an invite, sign in with no username, add a
  second passkey, revoke one.
- **Caddy conformance** (prior art: `VfsBridgeApiTests` reading the Caddyfile): every protected
  route goes through `forward_auth`, `/browse/*` doesn't, and the forwarded headers are copied, so
  a client-sent header can't survive.
- **Hub authorization** with forwarded headers: a person can't read, change, cancel or approve
  another person's topic; shared and sees-all visibility; review-copy ownership.
- **Channel resolution:** Telegram deep-link redemption, `/web`, `/bind` and an unlinked sender
  refused (fake bot client); a voice match resolving to the user and a non-match acting as nobody,
  with no memory extracted.
- **Keyed stores:** memory recall and extraction for one user across two channels; a web login
  reached from Telegram and WebChat by the same person and by nobody else; Sugary rows invisible
  across people while recipes are shared.
- **Approvals:** another person's answer is ignored; the asker's answer from a second channel wins
  and the first shows answered elsewhere; a fire's goes to the creator.
- **Migration** against a seeded Redis and Sugary database: users created and linked, memories
  merged, logins re-sealed and readable, schedules re-stamped, run twice changes nothing.
- **The E2E WebChat stack** signs in through the accounts service with a virtual authenticator, and
  the existing WebChat E2E suites run signed in. The eval harness stamps principals with user ids.

## Out of Scope

- More than one household, tenant config, tenant-brought keys, owner admin of agents and features:
  Tenants.
- Signed identity between services: Service identity. Here a server believes the principal from a
  caller holding its credential, and an app believes Caddy's headers.
- Enrolling a voice profile from the browser.
- Durable custom agents.
- Owner-facing usage views; the dashboard stays the operator's.

## Further Notes

Deploying needs the operator to run the migration once, naming themselves as operator and owner and
the person whose Sugary log it is, send each person their passkey link, and set the new accounts
secrets and the Caddy `forward_auth` hosts.
