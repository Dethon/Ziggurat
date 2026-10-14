# 12 — Full-stack E2E and switching her over

**What to build:** One end-to-end test on the compose stack: the app sends a meal photo, the real `sugary` agent answers through the Sugary channel, the meal is saved, it appears in `/sugary/meals` and the conversation appears as a WebChat topic. Then her phone moves from the standalone app to this one, once the eval tier shows the `sugary` agent no worse than the standalone app on the same meals, and her standalone log is imported.

**Blocked by:** 08, 10, 11

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The E2E passes on the compose stack and its wait on the model is bounded by the provider, as in the existing chat E2E.
- [ ] The eval tier's scorecards for photo and video are no worse than the standalone figures, and are kept with the switch.
- [ ] Her standalone meals and foods are imported and spot-checked in the app and in `/sugary`.

## Comments

**The E2E is written and compiles; it has not been run (2026-10-15).** It reaches a live model, so
running it is the user's call. None of the boxes above is ticked: the first needs a green run, the
other two need the switch.

`Tests/E2E/Sugary/SugaryE2ETests.cs`, one `[SkippableFact]` (`[Trait("Category", "E2E")]`, its own
collection `WebChatE2ECollections.Sugary`):

1. Signs in to the app's API with the access code (`POST /api/session`, cookie jar).
2. `POST /api/estimates` with the app's multipart form: one photo (`photos`, `meal-0.jpg`) and a few
   words carrying a per-run tag. The photo is Nutrition5k dish 1568061145, a slice of cheese pizza with
   squash (32 KB, CC BY 4.0, attributed in the test), committed as `Tests/E2E/Sugary/meal-photo.jpg`.
   It asserts that items came back, a total above zero, and a `conversationId` that parses as
   `chatId:threadId`.
3. Saves the meal as the app does (`POST /api/meals`, items as estimated, `firstEstimate` = the
   total, eaten now in Europe/Madrid).
4. Reads `/meals/<yyyy-MM>.csv` with `fs_read` over MCP on the Sugary server, behind the deployment
   secret, and finds the meal's row by its id. This is the file an agent with the mount reads as
   `/sugary/meals/<yyyy-MM>.csv`. No E2E fixture runs an agent's VFS, and asking the agent to read it
   would cost a second model call.
5. Opens WebChat in the **default** space (where a review copy's topic is minted), picks the Sugary
   agent, finds the topic by the tag, and checks her message, her photo's attachment (copied in the
   background, so it is waited for) and the reply.

**The wait on the model is bounded by the provider:** three minutes, as in `SendMessage_AppearsInChat`.
- The stack sets the server's `TurnTimeoutSeconds` to 180 (the shipped 120 is meant for her phone).
- The HTTP client waits a minute longer, so the server's 504 is what reports the cap.
- Either way, the test throws `TimeoutException`: "No estimate after 3 minutes. The estimate comes
  from a real model here (the sugary agent's, through OpenRouter), so this is a slow or failed
  generation rather than the Sugary channel or the app."
- Any other failure names the status and the server's `detail`.
- Before the agent's channel has first polled, the server answers 502 "not connected" at once, with
  no turn sent. That answer alone is retried, for up to a minute.

**The stack.** It is the shared WebChat stack (`WebChatStack`), which now also builds and runs
`mcp-sugary` (`E2EImages.McpSugary`, watching `Sugary.Client` too, since the image builds the app).
It is wired as compose wires it:
- `MCP__SHAREDSECRET` (the test secret), `SUGARYAPP__ACCESSCODE=e2e-sugary-code`, `TZ=Europe/Madrid`;
- a tmpfs at `/data`, so the shipped paths for the log, keys and uploads hold and start empty;
- port 8080 published for the app, since Caddy has no site for it yet, as in the deployment.

The agent's settings gain:
- the **shipped `sugary` agent definition**, read from `Agent/appsettings.json` and not restated, so
  the model, effort, features and prompt server are the deployment's;
- `agentDefaults.byChannel.sugary`;
- a `sugary` channel endpoint;
- `hostsReviewCopies: true` on the WebChat endpoint.

Every WebChat collection now sees a third agent, Sugary, in the rail. No sibling test was found to
count the agents or work out positions from them, but nothing has been run against three agents yet.
Jev's skill preload is on, as in the deployment. Checked by hand, with no model involved: the image
builds; the container starts on the tmpfs as the app user, signs in with the test code and lists
meals; with no agent connected an estimate is answered 502 with the "not connected" detail the retry
waits on. The E2E unit guards pass, including the image's watched directories and the ten collection
slices (this is the tenth, so the slices are now full).

Full suite (`Category!=Llm&Category!=Eval&Category!=E2E&Category!=EvalCalibration`): 6,744 passed,
0 failed, 0 skipped. PWA: `tsc --noEmit` clean, vitest 45 passed.

One side change: `TestHelpers`' staleness walk now skips `node_modules`. It is dockerignored, and an
`npm install` on the host would otherwise rebuild the Sugary image for nothing.

**How to run it** (needs Docker and the OpenRouter key, in `OPENROUTER__APIKEY` or the Tests project's
user secrets as `OpenRouter:ApiKey`; without the key it skips):

```bash
dotnet test Tests/Tests.csproj --filter "Category=E2E&FullyQualifiedName~SugaryE2ETests"
```

Add `PLAYWRIGHT_HEADLESS=false` to watch WebChat, `E2E_TRACE_FILE=<path>` for where the wall clock
went, and `E2E_AGENT_LOG_LEVEL=Debug` for the agent's own account of the turn. The first run builds
`mcp-sugary:latest` (node, .NET and the apt ffmpeg layer: minutes).

**Expected cost: about $0.01 per run.**
- One Gemini 3.8 Flash estimate at medium effort with one photo: $0.008–0.010, by ticket 08's
  figures (the standalone's $0.0066, plus the larger prompt and tools).
- Jev's preload judgment on that turn: a fraction of a cent.
- Nothing else calls a model. The review copy and the photo copy are MCP calls, the save is HTTP, and
  the CSV is rendered from the database.

A bare `--filter "Category=E2E"` runs this test too, beside the chat E2Es, which have always spent a
little on their own models. A routine run excludes `Category=E2E`.

**What a first green run settles that nothing else has:**
- the per-message strict format on Gemini through OpenRouter's Responses wire in a real turn
  (ticket 03 tested only the wire);
- a photo hydrated from the Sugary channel's store into a real request;
- the review copy minted in a real WebChat with the photo shown.

If it fails at the estimate with a 502 carrying a provider error, read that first: the format or the
image never reached Gemini as expected.

### Switching her over — checklist

- [ ] **Eval tier, paid run, approved first.**
  `ZIGGURAT_EVAL=1 ZIGGURAT_EVAL_MODEL=google/gemini-3.8-flash dotnet test --filter "Category=Eval&Tier=Sugary"`.
  - Photos must be linked and clips made (`Tests/Eval/Sugary/README.md`).
  - About $0.75–0.95 for one pass of the 78 meals plus the scenarios (tickets 08 and 10).
  - Compare `.eval-output/scorecard-sugary-<served model>.json` (`/` filed as `-`, e.g.
    `scorecard-sugary-google-gemini-3.8-flash.json`; `benchmark.byInput` and
    `bySetAndInput`) with the standalone figures. Photo: 12.3 g mean error, −8.3 g bias, median 23 s.
    `n5k` filmed against photographed: 8.3 g against 11.2 g. Compare the mean error first: the
    standalone in-range count predates the range floor.
  - Keep the scorecard with the switch (box 2).
- [ ] **The E2E above, green once** (box 1), with the command above (≈ $0.01).
- [ ] **HTTPS and a hostname** for the app (the hostname is your call), with a reverse-proxy site
  for `mcp-sugary:8080`.
  - The proxy must allow request bodies of about **200 MB** on `/api/estimates`. That is the clip
    ceiling (`Uploads:MaxClipBytes`) plus four photos.
  - It must forward `X-Forwarded-Proto`, so the cookie is marked Secure.
  - Installing the PWA and scanning with the live camera both need HTTPS.
  - Keep `/mcp` unexposed, or at least behind the deployment secret as it is now.
- [ ] **Set `SUGARYAPP__ACCESSCODE`** in `DockerCompose/.env`. Her standalone `AUTH__ACCESSCODE`
  value carries over; the name does not. While it is empty, every sign-in is refused.
- [ ] **Export her standalone log** from the standalone app's download screen:
  `sugary-meals.csv`, `sugary-items.csv`, `sugary-foods.csv`. Put the three files in
  `DockerCompose/volumes/sugary/import/`, then run
  `docker exec mcp-sugary dotnet McpServerSugary.dll import /data/import`.
  - It runs in one transaction. A second run adds nothing.
  - A cell it cannot read imports nothing and names the file and line.
- [ ] **Spot-check the import.**
  - In the app: the log's latest meals, a meal's items and first estimate, and her foods.
  - In `/sugary`: `meals/<month>.csv` and `items/<month>.csv` for a month she remembers, and
    `foods.csv`. Times carry the Madrid offset.
  - Meal photos do not carry over: the export has none.
  - This is box 3.
- [ ] **Reinstall the PWA on her phone** from the new HTTPS address, sign in with the code, and make
  one real estimate with a photo. Remove the standalone app's icon so she does not log in both.
