# 16: Migrate into the home household

**What to build:** Nobody starts over. One idempotent command, given the operator, the owner and the person whose Sugary log it is, creates one account per identity-map person with Telegram id, voice profile and chat-client name linked, and prints each a one-time passkey link. It merges each person's memories from every channel name into their account and the household bucket into the owner's, re-seals remembered logins under the user id, renames voice profiles, re-keys push subscriptions, re-stamps schedules' creators (to the owner where the old principal names nobody), gives today's Sugary rows to the named person, and marks every existing topic shared. Spec: `../spec.md` ("Migration").

**Blocked by:** 09, 10, 11, 14

**Status:** ready-for-agent

- [ ] Against a seeded Redis and Sugary database: accounts created and linked, memories merged, logins re-sealed and readable, schedules re-stamped, Sugary rows owned, topics shared
- [ ] A second run changes nothing
- [ ] The deploy steps are in the spec's Further Notes and the stack-launch instructions
