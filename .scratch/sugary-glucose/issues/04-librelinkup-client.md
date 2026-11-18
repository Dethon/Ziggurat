# 04: LibreLinkUp client and fake

**What to build:** `LibreLinkUpClient` in McpServerSugary and `FakeLibreLinkUp` in Tests/Integration/Fixtures. Login (headers `product: llu.android`, `version: 4.16.0`), region redirect re-login, `Account-Id` = sha256 hex of `user.id`, token cached until `expires`, re-login on 401. `connections` and `graph` parsed into typed readings (`FactoryTimestamp` UTC as the instant, `ValueInMgPerDl`, `TrendArrow` mapped) and sensor facts (serial, activation). Typed failures: credentials refused (status 2), step required (4, with the component name), version too old (920), rate limited (429/430 with `Retry-After`), unreachable. Spec: "Glucose from LibreLinkUp — Client".

**Blocked by:** None.

Status: ready-for-agent

- [ ] Against the fake: login, redirect, headers, connections, graph, token reuse across calls, one re-login on 401.
- [ ] Each failure maps to its typed result; `Retry-After` is surfaced.
- [ ] Trend arrows 1–5 and an unknown value map as the spec says; timestamps parse invariant-culture.
