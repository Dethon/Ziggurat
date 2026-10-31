# 02: Tunnel credentials

**What to build:** The accounts service mints a tunnel credential for one household and one server name by an operator command, shows it once, stores only its hash, verifies it on an internal API that answers the household and the server's name, and revokes it. Spec: `../spec.md` ("Credentials").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A minted credential verifies to its household and server name; an unknown, revoked or tampered one does not
- [ ] The store holds only a hash, and the credential is printed once
- [ ] The verify API is internal only and refused through the proxy on every host
- [ ] Revoking is a command and is reported to whoever is listening for revocations (the relay closes the tunnel in 03)
