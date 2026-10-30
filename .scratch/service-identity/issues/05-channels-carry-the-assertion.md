# 05: Channels carry the assertion; the agent verifies it at turn start

**What to build:** No turn acts for a person the accounts service did not vouch for. The chat hub takes the assertion from the session check's header, Telegram and voice from the resolution API's answer, Sugary from its request's header, and each carries it on the turn's notification. The agent verifies it once when the turn starts — signature, expiry, and that it names the channel the notification came on — refuses the turn and reports it if not, and holds the verified identity for the whole turn. Spec: `../spec.md` ("Channels and the agent", "Lifetimes and long turns").

**Blocked by:** 03, 04

**Status:** ready-for-agent

- [ ] Each channel's notification carries an assertion that verifies and names its person
- [ ] The agent refuses a turn with no assertion, an expired one, one naming another channel, or one not the accounts service's, and reports it
- [ ] A turn verified at its start keeps acting for its person after its assertion has expired
- [ ] A voice guest's turn carries a signed guest assertion and keeps the house working
