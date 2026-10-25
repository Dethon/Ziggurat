# 09: Logins page

**What to build:** A person can see and revoke what the agent can reach. A tool, `web_logins`, mints a one-time, ten-minute link for the asking person to a page under `/browse` listing their sites — remembered or live-only, with when each was last used — each with Forget. Forget drops that site's cookies and storage from the person context and from the saved state. No model tool forgets a login. Spec: `../spec.md` ("Logins page").

**Blocked by:** 08

**Status:** done

- [x] The page lists exactly the asking person's sites, never another person's
- [x] After Forget, an `asMe` browse of the site is logged out, both before and after a browser restart
- [x] The link follows the handoff ticket rules (single use, ten minutes, bound to the person)
- [x] `web_logins` refuses for an unmapped user
