# 07 — Labels read by the stronger model

**What to build:** She photographs a nutrition label and its table is read into carbs per 100 g and serving size. A label turn asks for the label's response format and carries an agent config patch naming the label model (Opus 5.5 today), which is one of the `sugary` agent's patchable models. A photo that is not a readable label is refused rather than guessed.

**Blocked by:** 05

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] A label turn's request uses the label model and the label schema; a meal turn is unaffected.
- [x] The label model is in the agent's patchable models; a model outside the list is never honoured.
- [x] A non-label photo comes back as unreadable and the app says so.
- [x] The read label can be saved to her foods with source `label`.

## Comments

**Built (2026-10-15).** `POST /api/labels` is a turn of the `sugary` agent on the label model. The
503 stub (`UnavailableEndpoints.cs`) is gone and `GET /api/session` says `labels: true`. The app's
form and answer are unchanged (`photo` as `label.jpg`, `language`; the standalone `PackagedFood`
JSON back), so **`Sugary.Client` did not change** and vitest was not rerun.

`McpServerSugary/App/LabelEndpoints.cs`, in order:

1. No `photo` part → 400 "Attach a photo of the label."
2. **A label model the agent may not switch to → 502**, before anything is stored or sent:
   "`<model>` is not a model the sugary agent may switch to, so this label cannot be read. Add the
   food by hand." (see the decision below).
3. `PhotoUpload.Refusal` (the meal's rules: JPEG/PNG/WebP, not empty, under 5 MB) and then
   `PhotoUpload.LabelModelRefusal`, which asks `AttachmentCapability.Refusal(agent, LabelModel, …)`.
   This is the patched model, as ticket 05 asked, not Gemini. Either one → 400 with the reason,
   nothing stored.
4. The photo goes to the upload store under a **fresh conversation id**, not through
   `OpenEstimates`. A label is read once, and her answers can never continue it.
5. `SugaryChannel.AskAsync` (which now takes an `AgentConfigPatch?`) emits the turn with
   `ResponseFormat = NutritionLabelSchema.Format`, `ConfigPatch = { Model = LabelModel }` (no
   effort, so the agent's `medium` stays, as the standalone used one effort for both), the
   reference, and `LabelTurn.Text(language)`.
6. The reply is read by `NutritionLabelReply.Read` (`Domain/Tools/Sugary/Labels/`):
   - It is strict like the estimate's reader: every field present and typed, `unit` only `g`/`ml`,
     nothing extra.
   - Then it applies the standalone rule. A label that is `readable` with carbs from 0 to 100 →
     200 `PackagedFood` (serving > 0 or none, a blank name or note is none, trimmed).
   - Anything else → **422** with the model's `note`, or "The label could not be read." when it
     gave none, as the standalone's endpoint did. The app shows a 422's `detail` as is, so its own
     localised "I couldn't read that label" is never reached: a reasonless refusal reads in
     English on a Spanish UI. If that matters, send an empty `detail` for that case and the app
     localises it (one line either side).
   - A reply outside the schema, an `Error` chunk, no answer, nobody polling → 502. Past the wait →
     504, with label wording ("add the food by hand").

`LabelModel` is a new `SugarySettings` member (`appsettings.json`, default
`anthropic/claude-opus-5.5`). `AgentAppSettingsTests.TheSugaryServersLabelModel_IsOneTheAgentMaySwitchTo`
pins it in `Agent/appsettings.json`'s `patchableModels`, and that the class default and the
shipped value agree. It also pins the literal Opus 5.5 id, as the ticket names it, so a deliberate
model change edits that test too.

**Decisions to know about.**

- **The label instructions ride on the label turn, not in the served prompt.** The standalone read
  labels with a system prompt of its own. Here `sugary_prompt` is the estimating prompt and reaches
  every turn. So `LabelTurn.Text` is one new lead sentence ("This turn is not a meal to estimate:
  the attached photo is a nutrition label to read.") followed by the standalone
  `LabelReader.SystemPrompt` and its user text, word for word. Putting them in `SugaryPrompt` would
  have cost every meal turn ~400 tokens, a budget bump and a snapshot. It would also have changed
  the prompt the eval measures. The model therefore still sees the meal prompt above a label turn.
  Whether Opus follows the turn over it is unmeasured: no real model was called.
- **A label model off the patchable list is refused by the server, not silently downgraded.** The
  agent never honours such a patch. `McpAgent.ResolveModelOverride` logs "Rejected config patch"
  and runs its own model, pinned by `McpAgentReasoningTests.RunStreaming_NonWhitelistedModelPatch_KeepsConfiguredModelAndWarns`.
  For a label that means Gemini reads it and the app shows the value as printed, with only a log
  line. That is the misread story 26 exists to prevent. So the server checks the catalogue the
  agent registers (`PatchableModels`, the same whitelist `McpAgent` uses, Lemonade models
  included): a label model neither on it nor the agent's own model → 502 before the send. Where
  the catalogue is silent (nothing registered yet), the label goes, as photos do. The settings test
  keeps the shipped value on the list, so this only fires on a misconfigured deployment.
- **422 for "not a label", 502 for "not an answer".** A refusal is a correct answer the app shows
  as its own message. An answer outside the schema is a failure, said the way an estimate's is.
- **`UnreadableEstimateException` is reused** for a label answer outside its schema. The sentence
  is the same, and the endpoint maps it the same way.
- **Ticket 06's minor note, fixed.** `FollowUpAsync` now refuses photos before `TryContinue`, so a
  refused answer no longer restarts the window
  (`AnAnswerWithPhotos_DoesNotKeepTheConversationOpen`). The side effect: a gone conversation that
  also carries photos now gets 400, not 410. The app never sends photos on a follow-up.
- **The label's photo stays in the store for the retention window**, like a meal's, even though
  nothing continues the conversation. It is what ticket 11's review copy would show.

**Tests.**
- `Tests/Integration/McpServerTests/SugaryLabelApiTests.cs` (real server, the agent played over MCP).
  Cases covered:
  - the label turn carries the config patch with `LabelModel` and no effort, the label format and
    schema, the exact `LabelTurn` text, no agent id, and one reference fetched back byte for byte.
    The reply comes in chunks, and the app gets name, carbs per 100, serving, unit and no `foodId`;
  - Spanish reaches the turn;
  - not a label / no reason / carbs 640 → 422 with the reason;
  - prose, a meal estimate, a missing field or an extra one → 502;
  - an `Error` chunk → 502 with its reason, and nobody listening → 502;
  - no photo → 400;
  - HEIC, empty and over 5 MB → 400 with nothing stored;
  - a label model that cannot see refuses the label while a meal photo still goes (with no patch);
    the reverse, a label model that sees, takes a label even though the meal model cannot;
  - a label model off the registered patchable list → 502, nothing stored, no turn; the label model
    being the agent's own → goes;
  - **saved to her foods**: the read label posted to `/api/foods` with `source: "label"`, listed by
    `GET /api/foods`, and its `/sugary/foods.csv` row ends `,label`.
- The first meal-estimate test now also asserts `ConfigPatch` is null: a meal turn carries neither
  patch nor label format.
- `SugaryAppServingTests.EstimatesAndLabels_AreBothAnswered` replaces the stub test.
- Unit tests:
  - `Tests/Unit/Domain/Sugary/NutritionLabelReplyTests.cs`: the standalone `LabelReaderTests`
    carried over, plus units, serving, blanks and 12 shapes outside the schema;
  - the schema is strict-mode valid (the check now lives in `StrictSchema.cs`, shared with the
    estimate's);
  - `LabelTurnTests`.

**Not done.** No real model was called. Whether Opus 5.5 reads a skewed label correctly through
this path, under the meal prompt, is unmeasured. So is whether it honours the strict schema with
`require_parameters` on the Responses wire. Ticket 08's eval or ticket 12's E2E is where that
first gets checked.

**Fixed (2026-10-15, with ticket 12): a reasonless refusal is now said in her language.** A label the
model refused with no `note` used to come back as 422 "The label could not be read.", which her
Spanish screen showed in English. `LabelReading.Unreadable.Reason` is now `null` in that case
(`NutritionLabelReply.CouldNotBeRead` is gone), and `LabelEndpoints` answers 422 with an **empty**
`detail`. Empty, not absent: the app's `request` reads `detail ?? title`, so an absent detail would
have shown the problem's title, "Unprocessable Entity". The app's 422 handling moved out of
`PackagedSheet` into `labelFailureMessage` (`Sugary.Client/src/errors.ts`): the model's reason as it
came, or `labelUnreadable` ("No he podido leer esa etiqueta.") when there is none. Pinned by
`SugaryLabelApiTests.ARefusalWithNoReason_IsAnsweredUnreadable_WithAnEmptyDetail_ForTheAppToWordInHerLanguage`
(null and blank notes; the body carries no English sentence), `NutritionLabelReplyTests`, and
`Sugary.Client/src/errors.test.ts` (the problem JSON read through the app's own `api.readLabel`: an
empty detail gives the Spanish string, a reason is shown as it came, a 502 as every screen shows it).
