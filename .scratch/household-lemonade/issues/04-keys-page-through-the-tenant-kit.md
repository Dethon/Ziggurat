# 04: The Keys page offers "through the tenant kit"

**What to build:** On the Keys page, a household owner chooses whether the Lemonade box is reached **at an address** (as now) or **through the tenant kit**. With the kit option the page asks for nothing more. It says what to set up: mint `lemonade` on the Servers page, then put the credential, the box's LAN address and an optional key in the kit's `.env`. Then it shows the tunnel's state. The admin API's Lemonade box route accepts either shape. A kit box is saved without a probe. Its status is the tunnel's state, asked through the accounts service's existing connected-servers client: connected, not connected, or unknown when the agent cannot be asked. An address box keeps its probe and key status. Saving one shape over the other replaces it, and removing works as now.

**Blocked by:** 03.

**Status:** done

- [ ] Saving a kit box through the admin API succeeds without probing anything, and the admin log records that the box is reached through the tenant kit.
- [ ] Saving an address box over a kit box (and the reverse) replaces it in one save, and the admin log says so.
- [ ] The household keys status reports a kit box with no address. Its connection state is "connected" when the agent's listing has the household's `lemonade`, "not connected" when it doesn't, and "unknown" when the listing cannot be had. Covered in the keys admin tests with a faked agent listing.
- [ ] Removing a kit box empties the section's box, as removing an address box does.
- [ ] Two owners saving the box at once follow the Keys page's existing merge-per-item and conflict rules.
- [ ] The WebChat Keys page's Lemonade slot has the choice. The kit option shows the setup hint and the connection state, and the address option shows the existing fields and status. Covered in the household keys E2E tests.
- [ ] The admin rules describe the Keys page's Lemonade slot with both shapes.
