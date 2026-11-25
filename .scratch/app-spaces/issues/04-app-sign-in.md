# 04: App sign-in

Spec: `.scratch/app-spaces/spec.md` ("App sign-in").

**What to build:** A person already signed in to Ziggurat opens a built app's link and lands in the app, signed in, with no second passkey prompt. A navigation on an app origin without an app session is redirected to a new route on the accounts service on the main host, which checks the person's ordinary session (passkey sign-in if there is none). It redirects back with a single-use code bound to that origin. The apps server redeems the code service-to-service for a person assertion on a new `apps` channel carrying `sid`, and sets a host-only `__Host-` cookie. App sessions are renewed by session refresh and end when the person's sessions end.

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] The accounts service issues codes only for origins under the configured apps domain, and refuses any other return target.
- [ ] A code is single-use, short-lived and redeemable only for the origin it was issued to.
- [ ] Redeeming answers an `apps`-channel assertion naming the person, the household and `sid`. The apps server believes nothing else about who the viewer is.
- [ ] The apps server renews assertions by the session refresh, as the chat hub does, and never after the session is revoked.
- [ ] `SessionsEnded` on the accounts channel ends every app session of that person.
- [ ] The apps server alone decides whether the signed-in person may open the app. Anyone else gets a 404.
- [ ] Accounts integration tests cover issue, single use, origin binding and redeem. An apps server test covers a session ending.
