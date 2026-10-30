# 01: Token signing and verifying, with published keys

**What to build:** One shared piece that signs and verifies compact JWS tokens with an asymmetric key and a key id, publishes an issuer's current and previous public keys as a JWKS document, and verifies against a JWKS it fetches and caches (refetching on an unknown key id, rate-limited). A fixed test key pair for the test and eval stacks. Prefactor for the rest of the phase. Spec: `../spec.md` ("Issuers and verifiers", "Keys", "Lifetimes and long turns").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] A token verifies with its issuer's key and fails when expired beyond the 30-second skew, aimed at another audience, tampered with, or signed by a key the JWKS does not hold
- [x] A guest token carries the guest marker and verifies like any other
- [x] A verifier fetches the JWKS once, serves later checks from its cache, and refetches when a token names an unknown key id, no more often than its rate limit
- [x] The test key pair is available to fixtures and never shipped in a deployment

Notes: ES256 on P-256, since .NET 10's BCL has no Ed25519. Everything is in
`Domain/Security/Signing/` (the one project every consumer reaches); `.claude/rules/service-identity.md`
holds the design and the public API. One fixed key per issuer in `Tests/TestSigningKeys.cs`.
