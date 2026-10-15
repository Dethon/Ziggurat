# Sugary: carb counting as a dual-role server and an agent of its own

Status: ready-for-agent

## Problem Statement

A person with type 1 diabetes counts the carbohydrate in every meal to decide her insulin, and she
eats out often. Counting by eye is slow and unreliable, so a standalone app called Sugary was built
to help. She photographs a meal, types a few words, or scans a barcode or a nutrition label. The
app returns every food with a carb range, asks a few questions about amounts, and keeps a log of
what she ate.

Sugary sits outside Ziggurat, and that costs three things:

- **The log is invisible to everything else.** No agent can read her meals, so nothing can put
  them beside the glucose history the Home Assistant mount already serves. Learning which meals
  were under-counted needs both.
- **The estimate is a one-off model call with its own plumbing.** Sugary has its own OpenRouter
  client, retries, cost tracking, model settings and evaluation harness, all of which Ziggurat
  already has. None of Ziggurat's model choice, metrics, dashboard or eval machinery reaches it.
- **The best input it measured cannot be sent.** On 14 weighed dishes, a short video around the
  plate cut Gemini 3.8 Flash's mean error from about 11 g to about 8 g (three runs each). Ziggurat
  has no video attachment, and the standalone app has no way to send one either.

Nobody reviews the estimates afterwards either: once a meal is saved, the conversation that
produced it is gone.

## Solution

Sugary becomes a **dual-role server** in Ziggurat, served by its own container, with its own
**agent definition**, `sugary`.

- **The app stays an app.** The Sugary PWA is served by the Sugary server and keeps its screens: a
  meal is built item by item, portions are edited, answers are tapped, the meal is saved.
- **Every estimate is a turn of the `sugary` agent.** The app sends the meal to the server, which
  is a channel server. The message reaches the agent like any other channel message, with the
  photos and the clip as attachments. The agent answers in a structured format the channel asked
  for on that message, and the server turns the answer into the estimate the app shows. Totals and
  the widened ranges on uncertain portions are computed by the server, never by the model.
- **Questions are follow-up turns of the same conversation.** When she taps an answer, the app sends
  it on the same conversation, and the agent estimates again knowing what it asked.
- **Nutrition labels are turns too**, with their own structured format and, through a patchable
  model, a stronger model than meal estimates use.
- **The estimates can be reviewed in WebChat.** Each estimate conversation is also a topic there,
  under the `sugary` agent.
- **Video goes to the models that take it, and frames go to the rest.** A clip is a new kind of
  attachment. The server normalises every clip on arrival and derives still frames from it. A
  model that takes video gets the clip; a model that takes only images gets the overhead photo and
  the frames, so a future model is not ruled out by its input types.
- **The tables are a mount.** The database is the only store. `/sugary` renders the meals, the
  foods in each meal and her saved foods as CSV on every read, read-only, so any agent with the
  mount can list, search and read them with today's file tools. Jonas has it, beside the glucose
  history `/ha` serves; the `sugary` agent does not.
- **Plain data stays plain.** Saving a meal, editing a portion, managing her foods, looking up a
  barcode and downloading the log are ordinary HTTP calls to the server, never agent turns.

## User Stories

### Estimating a meal

1. As the person counting carbs, I want to photograph my plate and get each food with a carb range,
   so that I have a starting point better than my own guess.
2. As the person counting carbs, I want to add a few words to the photo, so that what the camera
   cannot show (a sauce, a filling, a sugar-free drink) is counted correctly.
3. As the person counting carbs, I want to describe a meal in words alone, with no photo, so that I
   can count a meal I already ate or one I cannot photograph.
4. As the person counting carbs, I want to film a short clip around the plate, so that the estimate
   sees how high the food is piled and not only its outline.
5. As the person counting carbs, I want to send several photos of one plate, so that I can do the
   same without filming.
6. As the person counting carbs, I want the estimate to show a range and not one number, so that I
   know how sure it is.
7. As the person counting carbs, I want the meal's total to equal the sum of the items shown, so
   that the numbers I correct add up.
8. As the person counting carbs, I want uncertain portions to come with a range wide enough to hold
   the truth more often than not, so that a confident wrong number does not mislead me.
9. As the person counting carbs, I want each item marked with how sure the estimate is and why, so
   that I know which ones to check.
10. As the person counting carbs, I want the estimate in the language the app is set to, English or
    Spanish, so that I read it without translating.
11. As the person counting carbs, I want the estimate never to suggest an insulin dose, ratio,
    correction or timing, so that the app stays a counting aid and my care team decides my insulin.
12. As the person counting carbs, I want a note when a meal is high in fat or protein or mostly fast
    sugar, so that I can expect a late or a fast rise.
13. As the person counting carbs, I want a clear message when the estimate fails, so that I count by
    hand instead of waiting.
14. As the person counting carbs, I want an estimate in about the time the standalone app takes, so
    that the food is still warm when I eat.

### Answering questions

15. As the person counting carbs, I want the estimate to ask about the amount of the foods that
    carry most of the carbs, with answers I can tap, so that I can fix the part a photo shows worst.
16. As the person counting carbs, I want each offered amount to say the weight it means, so that I
    know what I am choosing.
17. As the person counting carbs, I want my tapped answers to produce a new estimate that keeps
    what was already established, so that I do not start again.
18. As the person counting carbs, I want a question never repeated once I answered it, so that the
    conversation moves forward.
19. As the person counting carbs, I want an amount I picked from the options treated as my rough
    judgement and an amount I typed treated as a fact, so that the range stays honest.

### Foods already counted

20. As the person counting carbs, I want foods I added by barcode, label or from my own list to be
    sent with the photo as already counted, so that the estimate does not count them twice.
21. As the person counting carbs, I want those foods to keep their own numbers whatever the estimate
    says, so that ground truth is never overwritten by a guess.

### Packaged food

22. As the person counting carbs, I want to scan a barcode and get the product's carbs per 100 g,
    so that packaged food is counted from its label and not estimated.
23. As the person counting carbs, I want a barcode I saved before to answer from my own entry first,
    so that a wrong public record cannot override mine.
24. As the person counting carbs, I want to photograph a nutrition label and have its table read, so
    that a product with no barcode record is still exact.
25. As the person counting carbs, I want a photo that is not a readable label to be refused rather
    than guessed, so that a wrong number never passes for a printed one.
26. As the operator, I want label reading to run on a stronger model than meal estimates, so that a
    skewed label is read correctly; one cheaper model misread a protein row as carbs.

### My foods

27. As the person counting carbs, I want to keep my own list of foods with their carbs and my usual
    amount, so that what I eat at home is counted exactly.
28. As the person counting carbs, I want to add a saved food to a meal in a couple of taps, with my
    usual amount, half and double offered, so that home meals are fast.
29. As the person counting carbs, I want to save a scanned or label-read product to my foods, so that
    the next time it is one tap.
30. As the person counting carbs, I want to edit and delete my foods, so that the list stays right.

### The log

31. As the person counting carbs, I want every saved meal kept with its items, its range, the first
    estimate and when I ate it, so that I can look back.
32. As the person counting carbs, I want to record afterwards whether my glucose ended in range,
    high or low, so that the log shows which meals were under- or over-counted.
33. As the person counting carbs, I want to download my log as tables, so that I can share it with
    my care team.
34. As the person counting carbs, I want my existing standalone log and foods carried over, so that
    nothing I recorded before is lost.
35. As the person counting carbs, I want the app to keep asking for the access code it asks for
    today, so that nobody else on the network can read or change my log.

### Video and frames

36. As the person counting carbs, I want any clip my phone records to be accepted, whatever format
    the phone uses, so that filming just works.
37. As the person counting carbs, I want the sound of a clip never sent anywhere, so that the
    conversation at the table stays private.
38. As the person counting carbs, I want a clip that is too long to be refused with a reason, so
    that I know to film a shorter one.
39. As the operator, I want a model that takes video to receive the clip and a model that takes only
    images to receive the overhead photo and frames from the clip, so that I can change the
    `sugary` agent's model without the app changing.
40. As the operator, I want which models take video to be discovered from the provider, the way
    image support already is, so that no list needs maintaining.
41. As the operator, I want the choice between clip and frames made for the model the turn actually
    resolved to, so that a patched model never receives an input it cannot take.
42. As the operator, I want a turn refused when its model takes neither video nor images, so that an
    estimate never silently ignores what was sent.

### Reviewing in WebChat

43. As the operator, I want each estimate conversation to appear as a topic in WebChat under the
    `sugary` agent, so that I can see what was sent and what came back.
44. As the operator, I want the photos and the clip of an estimate visible in that topic, so that I
    can judge an estimate against what the model saw.
45. As the operator, I want those topics to follow the same retention as every other topic, so that
    nothing special needs cleaning up.

### Other agents reading the tables

46. As another agent with the `/sugary` mount, I want her meals as one CSV per month, so that I read
    a month without reading years.
47. As another agent, I want the foods in each meal as one CSV per month, so that I can find what
    she ate and where each number came from.
48. As another agent, I want her saved foods as one CSV, so that I can see what she keeps at home.
49. As another agent, I want a README beside the tables explaining every column, so that I do not
    misread carbs as total carbohydrate or the first estimate as the whole meal.
50. As another agent, I want every time in the tables written with its offset in the home's zone,
    so that I can line meals up with the glucose history the Home Assistant mount serves.
51. As another agent, I want to search the tables for a food by name, so that "when did she last eat
    paella" is one search.
52. As another agent, I want the tables to show what is in the database at the moment I read them,
    so that nothing I read is stale.
53. As another agent, I want any attempt to write to `/sugary` refused, so that the only way to
    change her log is through Sugary itself.
54. As the operator, I want text a spreadsheet would run as a formula made harmless in every table,
    so that opening an export in a spreadsheet is safe.

### Operating it

55. As the operator, I want the `sugary` agent defined in configuration like every other agent, with
    the Sugary server as its only server and no tools at all, so that every estimate is one answer
    and it does not mix with Jonas's tools.
56. As the operator, I want the `sugary` agent to run with memory off, so that a recalled old meal
    never leaks into a new estimate.
57. As the operator, I want the Sugary server's prompt declared in the prompt manifest, so that it is
    budgeted and checked like every other served prompt.
58. As the operator, I want the cost and tokens of every estimate on the dashboard with every other
    turn, so that I can see what counting carbs costs.
59. As the operator, I want the server listed in the server table and the filesystem conformance
    tests, so that it is held to the same contracts as the others.
60. As the operator, I want to measure the `sugary` agent's accuracy on the benchmark meals for any
    model by name, so that I can compare models before switching.
61. As the operator, I want that measurement to say photo and video separately and per set of
    meals, so that a gain in one place is not hidden by another.
62. As the operator, I want an estimate whose structured answer does not match the format to fail
    as an error, never to be shown half-read, so that a broken answer cannot pass for numbers.

## Implementation Decisions

### The Sugary server

- A new **dual-role server** in its own container: a **tool server** in the sense of the hosting
  library (it serves a prompt, a skill and a mount) and a **channel server**. It is one new row in
  the server table and one in the filesystem conformance tests.
- It also serves, on routes other than `/mcp`, the Sugary PWA and its HTTP API. Those routes carry
  their own gate: the access code the standalone app uses today, set as a deployment secret. The
  deployment secret guards `/mcp` as on every server.
- The PWA moves into this repository as it is (React, TypeScript, Vite). It is a third browser
  client beside WebChat and the dashboard and shares no code with them; this does not touch
  ADR-0008, which is about those two.
- **Storage is SQLite, in the server's own volume**, through the relational model Sugary already
  has (meals, the foods in each meal, saved foods). Every other server store in Ziggurat is Redis.
  The departure is deliberate: these are relational tables read by date range and joined, and they
  are rendered as CSV. It gets an ADR of its own.
- What the server keeps of the standalone app: the meal and food model, the barcode lookup against
  the saved foods first and then Open Food Facts, the CSV rendering rules, the range floor (0.6× to
  1.6× of the best weight on every item not marked high confidence), and the estimate's prompt,
  answer schema and label schema. What it drops: its own OpenRouter client, retries and cost
  tracking. Every model call goes through the agent.
- An import reads the standalone app's three CSV exports (meals, items, foods) once and writes them
  into the server's database, keeping identifiers, times and sources.

### The channel role

- The channel's conversations are created by the app. Each meal being estimated is one
  conversation; the follow-up answers are further turns on it.
- A turn sent by the channel carries the person's text, the already-counted foods with their carbs,
  the language, the attachments, and **a response format** naming the schema the reply must follow
  (the meal estimate or the nutrition label).
- The channel keeps the bytes of every attachment itself, as an upload store, and answers the agent's
  attachment fetch like the SignalR channel does. Attachments cross every boundary as references
  (ADR-0020).
- A label turn also carries an agent config patch naming the label model, which must be one of the
  agent's patchable models.
- The reply arrives in chunks like any reply. The channel collects the whole reply, parses it
  against the schema it asked for, and only then computes the totals and widened ranges and hands
  the estimate to the waiting app request. A reply that does not parse, or a turn that ends with no
  reply, answers the app with an error.
- Delivery policy is `GateOnLive`: the app waits on its own request, so an estimate nobody is
  polling for is not buffered.

### The agent

- A new **agent definition**, `sugary`: the Sugary server as its only endpoint, memory and
  subagents off, Spanish and English handled by the language rule that already exists. Its model
  starts as Gemini 3.8 Flash, the best measured with video; its patchable models include the label
  model (Opus 5.5 today) and Haiku 5.5.
- **The `sugary` agent is a pure estimator with no tools.** Its definition says `noTools`: the Sugary
  server is dialled for the estimating prompt it serves and nothing else, so its requests carry no
  tool, no skill and no `load_skill`, it mounts nothing, and no skill preload judgment (Jev) is asked
  on its turns. Decided 2026-10-15 on the first paid pass of the eval tier: with the mount and the
  skill, estimate turns read her log and loaded the skill, 2 to 6 model calls per meal instead of
  one, $0.020 per estimate against the standalone's $0.0066, and 6 of 78 meals ended with no answer.
- `agentDefaults` sends the Sugary channel's messages to `sugary`.
- The estimating prompt is a **section served by the Sugary server**, declared in the prompt
  manifest under a name constant, with its budget, and for the `sugary` agent alone
  (`PromptAudience.ForAgents("sugary")`): any agent dialling the server would otherwise read it. The
  never-suggest-doses rule lives there, not in a skill, because it governs every turn.
- What the server can explain on demand (how her foods and the tables are laid out) is a **skill**
  the server ships, `sugary-log`. It is Jonas's: he dials the Sugary server for the `/sugary` mount
  and the skill, beside `/ha`, so a meal can later be lined up with her glucose. Dialling the server
  does not make him a consumer of its channel; `agentDefaults` routes the channel to `sugary`.

### Response format per message (a Ziggurat core change)

- A channel message may carry a response format: a schema name and the JSON schema itself, strict.
  It travels beside the agent config patch, is honoured for that turn only and changes nothing
  about the agent.
- The agent puts it on the chat options of that turn, and the chat client sends it on the
  Responses wire. Structured output is already used through OpenRouter by memory extraction and
  consolidation, never yet in an agent turn.
- A turn with a response format may still call tools before it answers; the format applies to the
  final answer. The `sugary` agent has none, so each of its turns is one answer.
- Provider routing on such a turn asks for providers that honour the parameter, as the standalone
  app does, so the format is never silently dropped.

### Video as an attachment kind (a Ziggurat core change)

- A third **attachment** kind, video, beside image and document. **Attachment capability**
  discovery maps the provider's `video` input modality to it, as it maps `image` and `file` today.
- The Sugary channel normalises a clip on arrival: H.264 in MP4, at most 540 lines, audio removed,
  at most 20 seconds (a longer clip is refused with that reason). The server's container carries
  ffmpeg for this; Domain stays free of codecs.
- On arrival the channel also derives about eight still frames from the clip, evenly spaced with
  blurred ones skipped, at the size the app uploads photos. They are stored as attachments of the
  same message, and the clip's reference names them as its stills.
- **Hydration chooses per turn**, for the model the turn resolved to. A model that takes video gets
  the clip and not its stills. A model that takes images only gets the stills and every photo of the
  message, never the clip. A model that takes neither has the turn refused, as an image is refused
  today. The choice is made where the send already checks image capability a second time (ADR-0029).
- A clip is hydrated within the same message distance as any attachment, so follow-up turns resend
  it.

### WebChat review (a Ziggurat core change)

- A turn arriving on the Sugary channel is also delivered into a WebChat topic under the `sugary`
  agent, created on the conversation's first turn: the topic shows the person's message with its
  photos and clip, and the agent's reply as it was sent. A structured reply shows as its JSON text.
- This is the fan-out that already exists for agent-initiated messages (the first target anchors
  the conversation, later targets attach to it), extended to messages a channel sends on a person's
  behalf when the channel asks for a review copy. The Sugary channel stays the primary target, so
  the answer is never waiting on WebChat.

### The `/sugary` mount

- A filesystem backend on the Sugary server renders, on every read and from the database:
  - `meals/<YYYY-MM>.csv`, one row per meal;
  - `items/<YYYY-MM>.csv`, one row per food in a meal;
  - `foods.csv`, one row per saved food;
  - `README.md`, explaining every column.
- Columns are those of the standalone export today:
  - **meals:** meal id, eaten at, carbs and its range, the first estimate and its range, outcome,
    items, notes;
  - **items:** meal id, eaten at, position, name, portion, amount, carbs per 100, carbs and its range,
    source, confidence, edited, food id;
  - **foods:** food id, name, carbs per 100, unit, portion amount, portion carbs, barcode, source.
- RFC 4180 quoting, UTF-8 without a byte-order mark, a leading formula character escaped with an
  apostrophe.
- Every time carries its offset in the home's zone, a setting of the Sugary server defaulting to the
  zone the Home Assistant mount uses.
- `carbs` is available carbohydrate, fibre excluded. `first_estimate` covers only the items the
  model estimated, so the model is judged against items whose source is `estimate`. The README says
  both.
- The backend overrides read, glob, info and text search, and nothing that writes: no workspace, not
  a landing target, no shell reach. A month with no meals has no file.
- Its reader is Jonas, with the `sugary-log` skill. The `sugary` agent dials the same server but
  calls nothing, so the mount is not made for it.
- Parquet is not rendered. A later format is another rendering of the same rows, never a second
  store.

## Testing Decisions

A good test here drives behaviour through a boundary something real uses: the app's HTTP API, the
MCP protocol the agent speaks, the request that leaves for OpenRouter, or a real model. It asserts
on what crosses that boundary, never on how the server arranges itself inside.

- **The Sugary server's boundary (most of the behaviour).** Boot the real server. Call its HTTP API
  as the app does, and play the agent over MCP: receive the channel message, fetch its attachments,
  send a reply, read the mount, read the served prompt and skill. Cover:
  - the round trip of an estimate and of a label: format on the message, reply parsed, totals and
    range floor computed, error on a reply that does not match;
  - already-counted foods on the message, and follow-up answers on the same conversation;
  - meals, foods, barcode lookup with a stubbed Open Food Facts, and the access code;
  - clip normalisation and the derived stills;
  - every CSV file and the README, write refusals, and the import.

  Prior art: the scheduling server's integration tests, the channel receive and send-reply contract
  tests, and the standalone Sugary API and export tests, which carry over as behaviour.
- **The agent's request to OpenRouter.** Through the chat client, assert that:
  - a turn whose message carries a response format sends it, and one without sends none;
  - a clip is sent to a model that takes video, and its stills and photos to one that takes images
    only;
  - a model that takes neither has the turn refused;
  - capability discovery maps `video`.

  Prior art: the chat client's hydration-depth and page-image wire tests, the model capability
  tests, and the agent reasoning tests that cover the config patch.
- **The WebChat review copy.** At the delivery resolver, assert that a channel turn asking for a
  review copy mints a WebChat topic on its first turn and attaches to it afterwards, with the channel
  first. Prior art: the chat monitor tests that already exercise delivery targets and conversation
  context.
- **Mechanical contracts.** One new row in the server table (settings singleton, host, one error
  filter, the `/mcp` gate); the filesystem conformance row (advertised operations match the
  overrides, no shell reach); the channel receive contract's row for its delivery policy; the
  prompt manifest's staleness and budget tests for the new section.
- **Behavioural eval against a real model.** A new eval tier, run per model by name like the
  Research tier and never by a bare test run:
  - it scores the `sugary` agent on the benchmark meals: 64 photographed meals from three public
    datasets with known carbs, and the 14 Nutrition5k dishes again as video;
  - it reports mean absolute error, bias, how often the truth falls inside the range, estimated
    against true weight, and time and cost per estimate, per set and for photo and video apart;
  - beside it, graded scenarios: the agent never suggests a dose, asks about amounts first, and does
    not relist already-counted foods.

  Before the app is switched to this path, the tier must show the `sugary` agent no worse than the
  standalone app on the same meals: about 13–14 g from a photo, 8.3 g with video on Gemini 3.8 Flash.
  Prior art: the Research tier and its graded scorecard, and the standalone benchmark's case files.
- **One full-stack E2E.** On the compose stack: the app sends a meal photo and receives an estimate
  produced by the real `sugary` agent through the Sugary channel; the meal is saved; it appears in
  `/sugary/meals`; and the conversation appears as a WebChat topic. It is bounded by the provider's
  time, as the existing chat E2E is. Prior art: the existing E2E fixtures and the chat send test.

## Out of Scope

- Glucose: reading the sensor, filling a meal's outcome from it, and any message the server starts
  on its own after a meal.
- A Telegram bot for the `sugary` agent, voice, or any channel but the app.
- Showing a structured reply in WebChat as anything but its text.
- Parquet or any format beside CSV.
- Insulin dose suggestions, ever.
- Changes to how estimates are made beyond moving them: prompt tuning, new questions, new models.
- Retiring the standalone Sugary repository.

## Further Notes

- The accuracy numbers come from the standalone benchmark of 2026-10-12 and 2026-10-13:
  - From a photo, every model tried scored about 13–14 g mean error.
  - With video, Gemini 3.8 Flash scored 8.3 g against 11.2 g from a photo on the same 14 dishes,
    three runs each, the gain coming from meals of 30 g or more.
  - Frames from the same clips gave Gemini 9.2 g but gave Haiku and Sonnet nothing.
  - Gemini 4 Argon, GLM 5.3 Flash and Qwen 3.8 Flash did no better with video.

  These are cafeteria dishes filmed by a rig, not her meals filmed by a phone: her own weighed meals
  remain the real test.
- The frames path exists to keep future models usable, not because frames are as good: a new model
  should pass the eval tier with frames before it is chosen.
- The estimate carries health consequences. The range floor, the totals computed in code and the
  refusal to read a non-label photo are safety properties, and each has a test at the server's
  boundary.
- Two ADRs come with this work: SQLite as the Sugary server's store, and a per-message response
  format on channel messages. The WebChat review copy may warrant a third, since it widens a fan-out
  that today serves only agent-initiated messages.
