# 14: Migrate today's data into the home household

**What to build:** One idempotent command, run once against the stack, that creates the home tenant and one user per identity-map person with Telegram id, voice folder and WebChat name linked, and prints each a one-time passkey link; takes the operator, the owner and the Sugary log's person as arguments. It merges each person's memories from every channel name into their user id and `household`'s into the owner's, re-seals web logins under the user id, renames voice folders, re-keys push subscriptions, re-stamps schedules' `CreatedBy` (to the owner where the old principal names nobody), assigns today's Sugary rows, and marks every existing WebChat topic shared. Spec: `../spec.md` ("Migration").

**Blocked by:** 06, 08, 09, 11

**Status:** ready-for-agent

- [ ] Against a seeded Redis and Sugary database: users created and linked, memories merged, logins re-sealed and readable, schedules re-stamped, topics shared (integration)
- [ ] A second run changes nothing
- [ ] The deploy steps are in the spec's Further Notes and the `launch-stack` skill
