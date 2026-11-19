---
paths:
  - "Agent/App/AgentApi.cs"
  - "Infrastructure/Agents/CustomAgentRegistry.cs"
  - "Infrastructure/StateManagers/RedisCustomAgentStore.cs"
  - "Domain/Contracts/ICustomAgentStore.cs"
  - "Infrastructure/Agents/AgentDefinitionProvider.cs"
  - "Tests/Integration/Agents/CustomAgentRegistryTests.cs"
  - "Tests/Unit/Agent/AgentApiTests.cs"
---

# Custom agents and the agent registration API

See also: `accounts.md` (the session check whose assertion this API believes, and SexyTime behind the proxy), `channel-identity.md`, `tunnel-credentials.md`.

## The agent registration API

`/api/agents*` is **not** behind the session check, on either host: its caller is SexyTime's server, which holds the agent API's secret (`AGENTAPI__SHAREDSECRET`) and forwards the `X-Ziggurat-Assertion` the check handed it for `sexytime` (`.scratch/service-identity/sexytime-note.md`).

- The API wants **both**: the secret says the caller is that server; the assertion — verified against the accounts service's JWKS, for `AppChannels.SexyTime`, a person not the guest — says who it acts for. Without a believable one the call is 401 even with the secret.
- It never reads `X-Ziggurat-User` or a `userId` parameter: the proxy does not check this route, so nothing strips a header a client wrote, and a holder of the secret relaying a client's headers could name anyone, but cannot sign.
- A custom agent is listed, used and deleted only under the id it was registered with. Its owner is the household **and** the person the assertion names (`Account(TenantId, UserId)`), so the same person id under another household is somebody else.

## Storage and resolution

Custom agents are **durable**: `RedisCustomAgentStore` keeps one hash per owner, `agents:custom:{householdId}:{userId}`, field the agent id (`custom-<guid>`), value the `AgentDefinition` as JSON, no expiry.

- `CustomAgentRegistry` answers every read from the process's copy — the reads beside the household's own agents are cheap and many (`IAgentFactory.CreateAsync`, the memory gates) — writes the store before the copy changes, and loads the store back as an `IHostedService` registered in `AddAgent` ahead of the channel connections, so a restarted agent has them by its first turn.
- A custom agent resolves only for its owner: `IAgentDefinitionProvider.GetByIdAsync`/`GetAllAsync` take the household and the owner, and asked with no owner answer the household's own agents alone (its configuration's, `.claude/rules/household-config.md`), each offered what the household's features leave it. So a turn acting for no account sees no custom agent (the factory does not fall back to the sender's name), and memory extraction, whose request names the household but no account, reads a custom agent as an unknown one (recall has already gated it with the turn's account).
- A registered agent may name a household server by its name; it is resolved against the turn's household as the household's own agents' are.
- Nothing was persisted before this, so the household migration has no step for them.
