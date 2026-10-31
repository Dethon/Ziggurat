# 02: Sealing in Domain

**What to build:** One AES-GCM sealing piece in Domain, beside the signing code, that seals a value under a key with associated data and opens it, so the accounts service can seal household secrets; remembered logins move onto it. Prefactor. Spec: `../spec.md` ("Configuration in the accounts service").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] A sealed value opens with its key and associated data and fails with either changed or tampered
- [ ] Remembered logins sealed before the move still open
