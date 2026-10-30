# 05: Channels carry the assertion; the agent verifies it at turn start

**What to build:** No turn acts for a person the accounts service did not vouch for. The chat hub takes the assertion from the session check's header, Telegram and voice from the resolution API's answer, Sugary from its request's header, and each carries it on the turn's notification. The agent verifies it once when the turn starts — signature, expiry, and that it names the channel the notification came on — refuses the turn and reports it if not, and holds the verified identity for the whole turn. Spec: `../spec.md` ("Channels and the agent", "Lifetimes and long turns").

**Blocked by:** 03, 04

**Status:** done

- [x] Each channel's notification carries an assertion that verifies and names its person
- [x] The agent refuses a turn with no assertion, an expired one, one naming another channel, or one not the accounts service's, and reports it
- [x] A turn verified at its start keeps acting for its person after its assertion has expired
- [x] A voice guest's turn carries a signed guest assertion and keeps the house working

Notes from the build:

- The hub does not take the assertion from the session check's header for its turns: a connection
  outlives that assertion by hours, so it asks the resolution API (`signalr`, the connection's
  account) for a fresh one per turn, and answers the stream with an error when the service cannot.
- The agent checks the assertion as each message arrives at its conversation group, not when the
  turn is dequeued: a message queued behind a long turn would otherwise be refused for the wait.
- A refused turn is also answered on its channel as a failed turn ends (error, then end of stream),
  so the browser, satellite or Sugary request waiting on it is released.
- Fires, login wakes and watches carry no assertion and keep signing as their stamped principal
  (the account it carries, else nobody), not as nobody: making them nobody now would break a login
  wake's asMe browsing and a schedule's memory until ticket 07, which replaces that fallback in
  `CallIdentity` and checks its delegation in `ConversationGroup.AdmitAsync`.
