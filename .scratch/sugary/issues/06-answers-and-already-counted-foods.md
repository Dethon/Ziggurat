# 06 — Answers and already-counted foods

**What to build:** When she taps answers to the estimate's questions, the app sends them as a follow-up turn on the same conversation and gets a new estimate that keeps what was established. Foods she added herself (barcode, label, her foods, by hand) travel with every turn as already counted, with their carbs, and are never listed again by the estimate.

**Blocked by:** 04

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] Answers arrive on the same conversation and the new estimate reflects them.
- [x] A question already answered is not asked again.
- [x] An amount she picked keeps medium confidence and a range; an amount she typed is treated as a fact.
- [x] Already-counted foods appear on the turn and never come back as estimated items; her own numbers are never changed.
- [x] The first estimate is still recorded with the meal when she saves it after answering.

## Comments

**Built (2026-10-15).** A meal is now one conversation. Its first estimate opens it, and her answers
are further turns on it.

1. **The contract, one field each way.** `POST /api/estimates` answers the estimate as before plus
   `conversationId` (`EstimateResponse` in `McpServerSugary/App/EstimateEndpoints.cs`, a wrapper so
   Domain's `MealEstimate`, which ticket 08 reuses, is unchanged). The app sends it back as a
   `conversationId` form field with her answers. A form without it is a first estimate, exactly as
   before.
2. **The follow-up turn.** `MealTurn.FollowUp` (`Domain/Tools/Sugary/Estimation/MealTurn.cs`). It
   has one new lead sentence ("The person has answered about the same meal. Estimate the whole meal
   again: keep what was already established and change only what their answers change."), then the
   standalone's own sections word for word: "Their answers to your earlier questions" and "Already
   added by the person and counted, so leave these out of your list". The language line closes it,
   as on every turn. It says nothing about photos. `MealTurn.Text`'s "There is no photo of this
   meal" would be false here, because the first message, which the agent still holds, has them.
   The first-turn text is byte-identical to before.
3. **No photos on a follow-up.** The turn carries no attachments. The first turn's references stay
   on its message in the agent's history, and hydration puts their bytes back within the 20-message
   distance (ticket 05's store answers the fetch, tested again after a follow-up). A follow-up that
   carries photos is refused with 400 and nothing is stored.
4. **Only conversations the server opened.** `OpenEstimates` (`McpServerSugary/Channel/`) records
   each conversation a first estimate opens. A follow-up continues one only within `FollowUpHours`
   (24, `appsettings.json`) of its last turn, and each answer restarts the window. Anything else (an
   id it never minted, a window passed, a restart in between) is answered **410 Gone** "This
   estimate is no longer open, so the meal has to be estimated again." before any turn is sent. This
   is tested with no agent connected, so it cannot be the 502 for "not connected". The check is in
   the endpoint, not in `SugaryChannel.AskAsync`, so ticket 07's label route does not go through it.
5. **The app** (`Sugary.Client`).
   - The draft keeps `conversation` from the latest estimate, and `reset` clears it.
   - `estimateForm` (`api.ts`) builds the form. A follow-up gets `conversationId`, her
     clarifications, what she counted and the language, and neither photos nor description.
   - `estimateDraft` (`estimate.ts`) sends the follow-up and, on a 410, estimates the meal afresh
     with everything, as the standalone always did. The new conversation then replaces the old one
     in the draft.
   - Any other failure is shown as before.
   - `clarifications` stays cumulative (every answer and correction so far), so the follow-up and
     the fresh fallback carry the same facts.
   - `counted` goes with every turn, first and follow-up, because she may have added a food after
     the first estimate.

**Decisions to know about.**

- **A food she counted that the model lists again is not filtered out, on purpose.** The server
  gets her foods only as the app's text lines (`- name, portion: N g of carbs`), and names contain
  commas ("white rice, cooked"), so a filter would have to guess at both the parsing and the match.
  Her product and food names are rarely the model's words in either language. A wrong match would
  silently drop a food from the meal and undercount it, and nothing on the screen would show it. A
  relisted food shows as an extra estimated row beside hers, which she can see and remove. So the
  guard is the prompt (the standalone's words: "do not list them again, even when you can see them
  in the photo"). The server returns what the model listed
  (`AFoodSheCountedThatTheModelListsAgain_ComesBackAsAnEstimatedItem_NeverDroppedUnseen`), and her
  numbers cannot be changed, because the server never returns her items and the app replaces only
  `source: "estimate"` rows (vitest: her yoghurt keeps 12 g beside a relisted one). Measuring how
  often the model relists belongs to ticket 08's graded scenario. If a filter is ever wanted, it
  needs the app to send counted foods as structured data first.
- **"Not asked again" is enforced at two levels.** In the prompt, through the standalone rule
  "never repeat a question that has already been answered", with every answer resent on the turn.
  In the app, through the existing filter: a returned question whose text she already answered is
  not shown. The server does not filter questions.
- **Picked vs typed is the prompt's job, and the range floor is code's.** A tapped option reaches
  the turn as `Q:/A:`. A typed amount reaches it as `Correction: "x" weighs N g.`. The prompt says
  to keep a picked amount at medium with a range, and to mark a stated amount high. Whatever
  confidence comes back, `MealEstimate.From` widens every non-high item to 0.6×–1.6× and keeps a
  high one as given (tested on a follow-up reply).
- **The first estimate** is still the app's to keep (`firstEstimate ?? total`, unchanged), including
  across a 410 fallback, which starts a new conversation but not a new meal. The boundary test saves
  a meal after an answer and reads back the first total over the API and in `/sugary/meals`.
- **`OpenEstimates` is in memory.** A restart forgets every open meal. The cost is one fresh
  estimate with the photos re-sent, and the app does that by itself. Persisting it (SQLite, or a
  signed id) did not seem worth it for a few minutes of conversation. Closed entries are pruned
  when a new meal opens, so the table holds at most one window of meals.
- **Hydration distance.** Only conversational messages count (tool calls do not), so the first
  turn's photos survive nine follow-ups. Three questions never get near that. Past it the model
  would see a placeholder; nothing guards against it.
- **The follow-up's lead sentence is new wording.** It is the only text not taken from the
  standalone, which never had a second turn. Whether it does better than resending the whole meal
  is unmeasured (ticket 08).

**Tests.**
- `Tests/Integration/McpServerTests/SugaryEstimateApiTests.cs`, with the agent played over MCP.
  Cases covered:
  - first turn then a follow-up: same `ConversationId`, a new `TurnKey`, the format again, the
    answers and the counted foods (one added since) in the text, the language line, no "photo" and
    no attachments, the first photo still fetchable, the new estimate returned with the same id;
  - a never-opened id (and a traversal-shaped one): 410 with no agent connected;
  - past the window: 410; each answer reopening the window;
  - a follow-up with photos: 400, nothing stored;
  - picked/typed ranges after an answer;
  - a relisted counted food;
  - a meal saved after answering keeping its first estimate.
- `MealTurnTests`: follow-up text.
- `Sugary.Client/src/estimate.test.ts` (8 vitest cases): the form both ways, the follow-up and its
  410 fallback, no fallback on other failures, the draft's conversation, and the first estimate and
  her numbers kept through an answer.

**Not done.** No real model was called. Whether Gemini 3.8 Flash keeps what was established, honours
"never repeat a question" and leaves counted foods out as a second turn of a conversation is
unmeasured until ticket 08. `photosGone` (follow-ups hidden after a reload that lost the photos) is
unchanged. With the conversation persisted in the draft, a follow-up would no longer need the
photos, but the 410 fallback would, so it stays.

