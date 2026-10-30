# 04: The dashboard, for the operator only

**What to build:** Memory text and usage per person stay the operator's: the proxy checks the session in front of the dashboard and admits only the operator. Spec: `../spec.md` ("The session check at the proxy").

**Blocked by:** 02

**Status:** done

- [x] A signed-in member is refused the dashboard; the operator reaches it (`SessionCheckTests.ARoleTheSessionLacks_IsForbidden`)
- [x] A signed-out request is sent to sign-in (`SessionCheckTests.NoSession_SendsAPageRequestToSignInAndBack`; the dashboard origin is allowed as a return)
- [x] The conformance check asserts the dashboard host goes through the session check
