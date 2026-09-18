# 02 — The Domain asks typed questions and one client answers

**What to build:** The typed-judgment contract in Domain — a state, named choice and noul questions, typed answers with probabilities and confidence, usage — and the one Infrastructure client that answers it from TypeSafe's `POST /v1/systemone`. A pooled warm connection, the caller's deadline honoured as a cancellation, 429/529/timeouts/transport errors answered as absence (never thrown into a caller), 401/422 logged loudly once as configuration errors. Settings `typeSafe: { apiUrl, apiKey, model }` in `Agent/appsettings.json` with `model` pinned to `jev-1.13.0`; the key a secret — placeholder in `DockerCompose/.env`, `${TYPESAFE_API_KEY}` on the agent in compose. An empty key registers a client that answers absence, so nothing downstream checks for the key itself. Test-first against a fake handler: the request body's exact shape, each answer type parsed, each failure mapped.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] A choice and two nouls asked together produce one request whose JSON matches the documented shape (`state`, `model`, `questions` keyed by id, `criteria` as a map for a choice).
- [x] Choice answers expose the pick, its confidence and the per-option probabilities; noul answers the probability; the response's model and input tokens are exposed.
- [x] 429, 529, a timeout, a cancelled deadline and a connection failure each answer absence and throw nothing; 401 and 422 answer absence and log an error naming the setting to fix.
- [x] An empty `apiKey` makes no HTTP call at all.
- [x] Domain references nothing of HTTP or TypeSafe by name; the contract carries no score type until a use needs one.
- [x] `DockerCompose/.env` holds a placeholder, compose wires `${TYPESAFE_API_KEY}`, and no real key is in any tracked file.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § The client and the contract.

## Answer

Shipped 2026-09-18.

- **Domain** `Domain/Judgments/`: `IJudge.JudgeAsync(JudgmentRequest, CancellationToken deadline) → JudgmentOutcome`
  (`Answered(Judgment)` | `Absent(AbsenceReason)`, reasons `Unconfigured` / `Deadline` / `Error`).
  `JudgmentRequest(JsonObject State, questions by id)`; `ChoiceQuestion(Instructions, Criteria)`,
  `NoulQuestion(Instructions)`; `ChoiceAnswer(Choice, Confidence, Probabilities)`, `NoulAnswer(Probability)`;
  `Judgment(Model, Answers, Usage(InputTokens, OutputTokens))`. No score type. Nothing names HTTP or TypeSafe.
- **Infrastructure** `Infrastructure/Judgments/TypeSafeJudge` (+ `TypeSafeOptions`): `POST systemone` with
  `{state, model, questions{id:{type, instructions, criteria?}}}`. 401 → error log naming `typeSafe:apiKey`,
  422 → `typeSafe:model`, each once per process (`Interlocked`); 429/529/5xx/transport/bad body → `Absent(Error)`;
  caller's token or `HttpClient.Timeout` → `Absent(Deadline)`. `TypeSafeJudge.Create` returns an
  `UnconfiguredJudge` (no HTTP, `Absent(Unconfigured)`) for an empty key.
- **Warm connection**: rides `HostedConnectionPool.Shared` (per-host pooling, 5-min idle) and gets its own
  `HostedConnectionKeepAlive` pinging `GET /v1/models` — verified 200 and non-billable on 2026-09-18 — under
  metric service `typesafe-connection-keepalive`. The keep-alive's endpoint and service name became options
  (`NonBillableEndpoint`, `MetricService`, defaults unchanged for OpenRouter).
- **Settings**: `typeSafe: { apiUrl, apiKey: "", model: "jev-1.13.0" }` in `Agent/appsettings.json`;
  `AgentSettings.TypeSafe`; `InjectorModule.AddTypeSafe`.
- **Secret**: `TYPESAFE__APIKEY=` placeholder in `DockerCompose/.env`. **Deviation from the ticket text**: the
  agent's secrets ride `env_file: .env` as `SECTION__KEY` (that is how `OPENROUTER__APIKEY` reaches it — nothing
  in compose says `${OPENROUTER__APIKEY}`), so the key follows that sibling rather than a `${TYPESAFE_API_KEY}`
  environment entry, which would have been the one secret wired differently from every other.
- **Tests**: `Tests/Unit/Infrastructure/Judgments/TypeSafeJudgeTests.cs` — 13 cases over a scripted handler.
