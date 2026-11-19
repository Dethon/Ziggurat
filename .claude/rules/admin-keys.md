---
paths:
  - "Accounts/Admin/KeysAdmin.cs"
  - "Accounts/Keys/**"
  - "Domain/DTOs/Admin/AdminProtocol.cs"
  - "WebChat.Client/Pages/Admin/HouseholdKeysPage.razor"
  - "WebChat.Client/State/Admin/HouseholdKeysStore.cs"
  - "WebChat.Client/State/Effects/HouseholdKeysEffect.cs"
  - "WebChat.Client/State/Effects/OnboardingEffect.cs"
  - "WebChat.Client/Components/Admin/CheckAgainButton.razor"
  - "WebChat.Client/Components/Admin/KeyStatusView.razor"
  - "WebChat.Client/Components/Admin/KeyFormNotice.razor"
  - "Infrastructure/Agents/ChatClients/RefusedKeyReportingChatClient.cs"
  - "Infrastructure/Agents/ChatClients/IHouseholdKeyReports.cs"
  - "Tests/Integration/Accounts/KeysAdminTests.cs"
  - "Tests/Integration/Fixtures/FakeKeyProviders.cs"
  - "Tests/E2E/WebChat/HouseholdKeysE2ETests.cs"
---

# The admin API: keys and probes

See also: `admin.md` (routes, gate, answers, change log, the admin area), `admin-owner-pages.md`,
`admin-operator.md`.

## Keys and probes

The keys page (`KeysAdmin`, over 4b's `keys` section) saves each key on its own route — `GET /household/keys`
(`HouseholdKeys`), `PUT /household/keys/openrouter` (`OpenRouterKey`), `/household/keys/idealista`
(`IdealistaKeys`), `/household/keys/telegram/{agentId}` (`TelegramBotKey`), `/household/keys/lemonade` —
**after its provider was asked** (`Accounts/Keys/KeyProbes.cs`):

- **One contract, one probe per provider**: `IKeyProbe<TKey>` answering `KeyProbeOutcome.Working`,
  `Rejected(why)` or `Unreachable(why)`. `HttpKeyProbe<TKey>` is the one-request kind: 2xx works; the
  probe's `Refusals` reject (401/403; Idealista adds 400 and OAuth's `invalid_client`; Telegram 404 and a
  token it cannot read); **anything else** — 5xx, 429, a timeout (`KeyProbes:Timeout`), no connection — is
  unreachable.
  - OpenRouter: `GET {OpenRouterUrl}key` with the key as bearer (bills nothing).
  - Idealista: its token exchange.
  - Telegram: `getMe`, after the token is checked against BotFather's shape (it goes into a URL path).
  - Adding a provider is one probe and one `KeySlot`.
- **The base URLs are settings** (`KeyProbes:OpenRouterUrl`, `IdealistaUrl`, `TelegramUrl`, `Timeout` in
  `Accounts/appsettings.json`, real hosts by default), so a stack points them at a stub (`FakeKeyProviders`;
  the E2E stack's `KEYPROBES__*` at one in process through the host's gateway). The probes' HTTP client
  (`key-probes`) has its request loggers removed: a bot's token is in the URL.
- **The household's own Lemonade box** (`PUT`/`DELETE /household/keys/lemonade`, `LemonadeBox`), one of two
  shapes, either replacing the other:
  - **At an address**: probed by its model listing with its key (`LemonadeBoxProbe`); a non-home
    household's must be a public https address (`LemonadeAddresses`; a test stack lifts it with
    `KeyProbes:LemonadeOnPrivateAddresses`). Shown as `HouseholdKeys.Lemonade`, its address and key state.
  - **Through the tenant kit** (`LemonadeBox.Kit`, `{ throughKit: true }`): a box behind the household's
    router reached through its kit's forwarder, held to neither rule and **saved unprobed** — there is no
    address to ask. Recorded "Set the Lemonade box, through the tenant kit"; shown with `ThroughKit` and its
    forwarder tunnel's `Connection` (`ServerConnection`, asked of the agent's relay through
    `ConnectedServersClient`; `Unknown` when it cannot be asked). Its `lemonade` credential is minted on the
    servers page (`admin-owner-pages.md`, "Own servers").
  - The page's slot offers "At an address" or "Through the tenant kit", the latter asking nothing and saying
    what to set up in the kit.
- **A save**: the section's own rules first (a blank key refused in their words, nobody asked), then the
  probe.
  - **Rejected refuses the save**: 400 `AdminRefusal` "The X was not saved: <why>.", nothing saved or
    recorded.
  - Working or unreachable saves through `HouseholdConfigStore.SaveAsync` (section read whole, the one key
    put, saved whole: others kept, every secret sealed again), then the check is stored and the change
    recorded ("Set the OpenRouter key" / "Replaced …", "the Idealista keys", "the Telegram bot for
    <agent>").
  - Unreachable answers `AdminSaved.Warning` ("could not be asked …: it shows as unchecked"), joined with
    the not-told warning when both apply.
  - A bot must be bound to one of the household's agents (`HouseholdAgents.Names`, the two templates for a
    household that saved none). A bot's token saved under another agent moves there (`KeySlot.TelegramBot`
    drops the token's other binding: one bot is never polled twice).
  - No master key: a read or a save answers **503** `AdminRefusal` with
    `KeysAdmin.SecretsUnavailableMessage` — the deployment's to fix, never a 500 or 400; the chat client
    shows the message (`AdminAnswer.Unreachable(message)`).
- **Check again** (user, 2026-11-13): `POST .../openrouter/check`, `.../idealista/check`,
  `.../lemonade/check`, `.../telegram/{agentId}/check` (`AdminProtocol.*Check`) put the **stored** key to
  its provider — the page sends none and is answered none — and record the result as a save's check is:
  working; rejected with the provider's why (the key stays saved; a warning says so); or unchecked with the
  reason (a warning). Nothing is saved or announced; the log says "Checked the <label>". No key saved is
  404; a box through the tenant kit 400 (no address). It is how a key saved by the household migration or
  the operator's command, which asked no provider, comes to show as working. The page draws a Check again
  button beside every saved key's status (`CheckAgainButton`), one save or check at a time
  (`HouseholdKeysState.Checking`).
- **State lives beside the section, not in it**: `HouseholdKeyChecks` (table `HouseholdKeyChecks`, one row
  per household and key name — `openRouter`, `idealista`, `telegramBots/<agent>`): state, when checked
  (`AccountsClock`), a reason, and the key's **fingerprint** (`Tokens.Hash` of the key, Idealista's of its
  pair). Not in the section because a state change is not a save — every save is announced and drops every
  reader's cache, Telegram's bots included — and the operator's `get`/`set-household-config` and the seed
  route stay as 4b made them. A key whose fingerprint is not the stored check's (the operator's command
  replaced it) reads **unchecked** with no check time.
- **Never the key**: `HouseholdKeys { paysWithDeploymentKey, openRouter, idealista, telegramBots,
  needsOpenRouterKey, lemonade, isHome }`, each key a `KeyStatus { state, lastCharacters, checkedAt, reason }`
  — `AdminProtocol.KeyCharactersShown` (4) last characters, none under `ShortestKeyShown` (12), Idealista's
  from its API key alone, never its secret.
  - `needsOpenRouterKey`: not paying with the deployment's key and no OpenRouter key — i.e. a non-home
    household without one.
  - `paysWithDeploymentKey`: the home with no key of its own; one saved for it takes priority
    (`HouseholdAgents.Payer`), and the page says the deployment's runs only until one is set.
  - `isHome` picks the Lemonade slot's wording (the home with no box runs on the deployment's, and its own
    may be on its network).
- **A key failing in a turn**: `RefusedKeyReportingChatClient` wraps the OpenRouter client of a turn on a
  household's own key (`MultiAgentFactory.CreateChatClient` with a key and its household; never the
  deployment's key, never the Lemonade box). On a `ClientResultException` 401 — not 402, which is credit —
  it calls `IHouseholdKeyReports.OpenRouterKeyRefusedAsync(household)` once per client, without waiting.
  - The turn fails as a `TurnRefusedException` carrying `RefusalAnswers.KeyRefused` (a 402, `OutOfCredit`,
    is not reported but refused the same way), shown bare — never the provider's status line — and spoken
    in Spanish by voice (`SpokenRefusals`).
  - `HouseholdConfigurationClient` implements it (registered only where an agent token is given):
    `POST /accounts/internal/household-config/openrouter-key-refused`, proven as the agent's own view is
    (`InternalCallers`' view table, `HouseholdProofs.AgentAsync`), answered 204. An internal route rather
    than a Redis event: Redis messages carry no proof of sender, and accounts subscribes to nothing.
  - `KeysAdmin.InUseRejectedAsync` marks the saved key rejected, then probes it: working heals it, rejected
    keeps it with the provider's why, unreachable leaves it rejected.
- **Onboarding**: `OnboardingEffect`, on `SignedIn`, asks an owner's keys (outside the admin area only — the
  area signs in too) and, when `needsOpenRouterKey`, enters `AdminArea.Keys` by a full load. The keys page
  says why there (`role="note"`).

## Tests

Seam 1 is `KeysAdminTests` against `FakeKeyProviders`; the turn side of a refused key is
`MultiAgentFactoryHouseholdTests` (`FakeOpenRouter.Refuse`). `HouseholdKeysE2ETests` submits only a key the
stub rejects, and shows the landing by answering the page's keys read as a keyless household's would be
(`page.RouteAsync`); nothing there saves a key (the home's turns read its keys: `admin.md`, "Tests").
