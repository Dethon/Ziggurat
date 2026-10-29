# 07: CORS admits only the deployment's origins

**What to build:** A page from a foreign origin can no longer drive the agent registration API or the hub with a visitor's credentials. Both hosts read their allowed origins from a per-deployment setting; only those get CORS headers, with credentials; an empty list means same-origin only. Spec: `../spec.md` ("CORS").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] A preflight from a listed origin is answered with CORS headers, and from a foreign origin without, on both hosts
- [x] An empty setting answers no cross-origin preflight
- [x] The chat client still connects to the hub through the proxy (E2E suite passes)
