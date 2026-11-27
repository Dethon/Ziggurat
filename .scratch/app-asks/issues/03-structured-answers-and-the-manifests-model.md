# 03: Structured answers and the manifest's model

Spec: `.scratch/app-asks/spec.md` ("The origin's ask route"; "The app manifest gains three fields").

**What to build:** An app passes a strict JSON schema on an ask and gets parsed JSON back, as
Sugary's estimate does (ADR 0044). An app whose manifest names `model` asks on that model of the
building agent's patchable models.

**Blocked by:** 02

**Status:** ready-for-agent

- [x] `apps.ask(text, { schema: { name, schema } })` puts the schema on the message as the response format. The agent sends it strict and as written (origin and agent seams).
- [x] A reply that parses resolves as the parsed value. One that does not is the `invalid_reply` code; its raw text reaches the author's preview only, never a member.
- [x] A schema that is not an object, or is over the size bound, is refused before any turn.
- [x] `AppManifest` takes `model`. It travels as the config patch, and a model the agent cannot patch to answers on the agent's own model, as any patch does (agent seam).

## Comments

**Done (2026-11-26).** What 04 and 06-11 build on:

- **The request** (`AppAskRequest`): `{"text": "...", "schema": {"name": "...", "schema": {...}}}`, `schema`
  optional, parsed strictly (inside `schema` too: a `strict` field is refused). The name is
  `[A-Za-z0-9_-]{1,64}` (`AppAskSchema.MaxNameLength`); the schema a JSON object of at most
  `Asks:MaxSchemaBytes` (8,192, as written, UTF-8). Anything else is 400 `invalid_argument` before any
  turn. Strict mode's own rules (every property required, `additionalProperties: false`) are not checked:
  the schema goes out as written (ADR 0044), and a provider refusing it fails the turn (422
  `transient_dependency`). The body bound now adds `MaxSchemaBytes`.
- **The message**: the schema is `ResponseFormat { Name, Schema }`; the origin's own manifest's `model` (served
  commit's / tree's) is `ConfigPatch { Model }`. Neither is sent when absent. A shared app's `model` is not
  narrowed by its approval (an approval names servers only).
- **The answer**: without a schema 200 `{conversation, text}` as before; with one 200 `{conversation, value}`,
  the last message's text parsed as JSON whole (`AppAsks.ReadReply`: no fence stripping, no check against
  the schema). Never both keys (`AppAskAnswer`, null keys omitted). A reply that does not parse is **422
  `invalid_reply`**, a new `ToolError.Codes` entry, **not retryable** (asking the same way again tends to get
  the same answer; a hint says so). Its words ride as the envelope's **`reply`** field (`AppAsks.ReplyField`,
  clipped at `AppAsks.MaxReplyShown`, 4,000 characters) **on the preview alone** (`session.Origin.Preview`):
  the published origin carries none, its author included, and nothing logs them.
- **The client library**: unchanged in shape: `apps.ask(text, { schema })` sends it, resolves the answer
  object (`value` when a schema was sent), and `AskError` gains `reply` (from the envelope).
- **`AppManifest.Model`** (`model` in `Fields`): shape alone (`vendor/name`, a slash neither first nor last,
  no whitespace or control characters, at most `AppManifest.ModelLength` 200), kept as written. `./publish`
  does not check it against the building agent's `patchableModels`, so an author learns of an off-list model
  only from the agent's log (`Rejected config patch model=...`); 10/11 may want `./check` or the skill to say
  so. **A `lemonade/` model the household's box does not offer fails the ask** (the agent's existing rule:
  `LemonadeChatHostException`), reaching the page as 422 `transient_dependency`, not a fallback; noted for 10.
- **Agent seam** (`AppTurnAgentTests`): the two new cases passed on first run, as expected, since the agent
  path already existed: a schema goes out `json_schema`, strict, as written (its `minimum` kept), with
  `provider.require_parameters`, the offered tools not strict; a patchable model (any casing) answers the ask,
  an unknown hosted one the agent's own. The fixture's `OpenRouterConfig` now names one patchable model.
- **Tests**: `AppAsksTests` (request shapes, reply reading, `InvalidReply`), `AppManifestTests` (model),
  `AppsAsksTests` (schema sent and value answered; `invalid_reply` on preview vs published; the schema
  bound beside a text at its bound; the model from the published vs the preview manifest; schema refusals),
  `AppsServingInABrowserTests` (the one Chromium case also asks with a schema and reads `value`),
  `AppsShippedSettingsTests` (the new setting shipped). The `apps` skill is unchanged: no name it teaches
  changed (11 teaches schemas, `model` and `invalid_reply`).
