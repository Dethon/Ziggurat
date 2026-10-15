# 10 — Filming a meal

**What to build:** In the app she films a short clip around the plate. The Sugary channel normalises it on arrival (H.264 in MP4, at most 540 lines, no audio, at most 20 seconds, a longer clip refused with that reason) with ffmpeg in its container, derives about eight sharp, evenly spaced stills at the app's photo size, and sends the clip with its stills on the turn. Gemini 3.8 Flash gets the clip; an image-only model gets the overhead photo and the stills. The 14 Nutrition5k video cases join the eval tier.

**Blocked by:** 05, 08, 09

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] Clips in the formats phones record (MP4 with H.264 or HEVC, WebM) are accepted and normalised; the sound never leaves the server.
- [x] An over-long clip is refused before anything is sent, and the app says why.
- [x] The stills skip blurred frames and are stored as attachments of the same message, named by the clip's reference.
- [x] On Gemini 3.8 Flash the request carries the clip; with the agent patched to Haiku it carries the stills and photos.
- [x] The eval tier reports video and photo apart; a first run is compared with the standalone figures (8.3 g with video against 11.2 g from a photo on Gemini 3.8 Flash).

## Comments

**Built (2026-10-15), with no model called.** A meal can now be filmed. In order, on
`POST /api/estimates` (`McpServerSugary/App/EstimateEndpoints.cs`):

1. **The app** (`Sugary.Client`). A "Film the plate" control (`<input type=file accept="video/*"
   capture="environment">`) sits below the photo buttons, with a hint saying up to 20 s and that
   the sound is never sent. It holds one clip, shown as a tile beside the photos with a remove
   button. `estimateForm` sends it as a `clip` part under the phone's own file name and type. It goes
   with a first estimate and with the fresh one after a 410, never with her answers. Strings are in
   English and Spanish. vitest pins the form and the draft's sends.
2. **Before anything is kept**, in this order:
   - the photos' rules;
   - `ClipUpload.Refusal`: one clip per meal, not empty, at most `Uploads:MaxClipBytes` (200 MB);
   - the model check, which refuses a clip only for a model that takes neither video nor images
     ("`<model>` can neither watch a clip nor read photos, so this meal cannot be estimated from
     it…");
   - then `ClipProcessor` (`McpServerSugary/Clips/`) reads the clip with ffmpeg. A clip over 20 s is
     answered 400 "The clip is 25 seconds long. Film at most 20 seconds around the plate, or take a
     photo instead.". Anything that is not a clip gets 400 "That clip could not be read. Film it
     again, or take a photo instead.".

   Each of these comes back with nothing stored and nothing sent, tested with no agent connected.
   The app shows `detail` as written, in English as for photos.
3. **Normalised**: `-map 0:v:0 -an -sn -dn -map_metadata -1 -map_chapters -1`, short side at most
   540, `-fpsmax 30`, libx264 `veryfast` CRF 28, yuv420p, `+faststart`, cut at 20 s. The phone's
   rotation flag is applied, so an upright clip stays upright. The sound and the phone's metadata
   (GPS among it) never reach the store.
4. **Stills**: up to `Clips:Stills` (8), taken from the *original* picture, not the 540-line clip,
   at most 1568 px on the long side (the app's `UPLOAD_EDGE`), as JPEG. How they are chosen:
   - one ffmpeg pass measures `blurdetect` on about four moments per still wanted, on a 640-px copy;
   - `StillChoice` (pure, unit-tested) cuts the samples into eight even stretches and takes the
     sharpest moment of each;
   - it drops a stretch whose sharpest moment is more than 1.6 times as blurred as the clip's
     sharpest. A frame with no edges counts as blurred. The sharpest stretch always gives one still;
   - each still is then extracted with `-ss <t>`.

   A still is named `still-<n>-<seconds>s.jpg`, which WebChat's review shows and the boundary test
   reads.
5. **Stored and sent**: the clip (`meal.mp4`, `video/mp4`) and each still go to the upload store
   under the meal's conversation, beside its photos. The turn's `Attachments` are `[photos…, clip
   { Stills = still ids }, stills…]`, the shape in ticket 09's comment. `MealInput.Filmed` adds the
   standalone benchmark's video sentence ("A video of the meal is attached, filmed while moving
   around the plate." or "A video of the same meal is attached too, …" after her photos) and one new
   sentence: "If you are shown still frames instead, they were taken from that video.".
   `PhotoCount` still counts her photos alone. With no words and no photo, the review topic is
   titled `Meal video`.
6. **Follow-ups** refuse a clip as they refuse photos ("Answers go without a clip…"). The first
   turn's clip and stills are hydrated from history like photos.

The ffmpeg arguments live in the server only. Domain gained `MealInput.Filmed` and the wording, and
no codec.

**Tests at the server's boundary** (`Tests/Integration/McpServerTests/SugaryClipApiTests.cs`, the
agent played over MCP; clips made per run by the local ffmpeg in `Tests/Integration/Fixtures/
TestClips.cs`, each test `[Skippable…]` with the reason "ffmpeg is not installed on this machine"
where it is absent). Covered:
- a clip with sound reaches the turn naming 6–8 stills, each an attachment of the same message,
  fetched through `ChannelAttachmentSource`: JPEG, at the phone's 1280×720;
- the fetched clip is H.264 in MP4 at 960×540 with exactly one stream (no audio);
- HEVC portrait MOV, VP9/Opus WebM and a browser recorder's WebM with no stated length are accepted
  (a 25 s one of those is still refused, its length read off the last frame), and a clip stored sideways with a rotation flag
  arrives upright as 540×960;
- 25 s is refused with its reason and nothing stored; 20.3 s goes, cut to at most 20 s;
- a clip blurred from 2.5 s to 5.5 s gives 5–6 stills, none taken from the blurred window
  (mutation-checked: with the tolerance lifted it fails);
- photo plus clip: the text counts one photo, and all files sit under one conversation;
- refusals for not a video, empty, two clips, over the size ceiling, a model that takes neither
  (and the same clip going once the model sees pictures), and a clip on an answer;
- a 40 MB body reaches the clip's own rules, not a 413;
- an image whose ffmpeg is missing answers 500 "Clips cannot be read on this server right now…"
  with nothing stored;
- **the seam**: the real turn's attachments, through `ClipSelection.For`, give a video model the
  clip and her photo (no stills), and an image-only model the stills and her photo (no clip), with
  no refusal. Ticket 09's wire tests already pin the request bodies over hand-built references.
  This test pins that the references this channel sends drive the same choice.

Unit: `StillChoiceTests` and `MealTurnTests` (both video wordings). Full suite
(`Category!=Llm&Category!=Eval&Category!=E2E&Category!=EvalCalibration`): 6,739 passed, 0 failed,
0 skipped (before the two recorder-WebM cases were added; `SugaryClipApiTests` then 20/20). PWA: `tsc --noEmit` clean, vitest 42 passed.

**Docker.** `McpServerSugary/Dockerfile` installs `ffmpeg` (apt, `--no-install-recommends`) in the
final stage as root, then drops back to `$APP_UID`. It was built once against a private base tag. In
the image (Ubuntu 24.04, ffmpeg 6.1.1, as the app user), `blurdetect`, libx264 and mjpeg are present,
and a normalise and a still extraction with the whitelists both ran. The image is 331 MB of content.
The images were removed afterwards.

**Eval.** `Tests/Eval/Sugary/video-cases.json` holds the 14 `n5k` dishes again, named
`n5k <dish> (video): …`. Each has its photographed case's truth (pinned), no photo, a clip and its
stills. `SugaryCases.Shipped` is both files, so the tier is now 78 meals. Each filmed meal is sent
as the channel sends one: `EvalAttachment.Stills` names the stills by path, and
`EvalAttachments.References` turns those paths into ids. A filmed meal counts as seen when its clip
**or** every still was served; otherwise a video model would fail every filmed meal as "only 1 of 9
photos reached the model". A missing clip or still skips the tier like a missing photo.
`byInput` / `bySetAndInput` already split the scorecard.

**The 14 clips are ready on this machine.** `Tests/Eval/Sugary/fetch-clips.sh` downloaded
Nutrition5k's side-angle `.h264` files and made the clips and stills the standalone's way:
- the downloads are 315 MB, cached in `~/.cache/ziggurat/nutrition5k`;
- the output in `Tests/Eval/Sugary/clips/` (gitignored) is 33 MB: 14 clips and 108 stills;
- the recipe was recovered from that session's commands: cameras at 4 frames a second, concatenated
  at 1280×720 with CRF 26, then 960×540 with CRF 29 and `slow`; stills at ¼ and ¾ of each camera,
  1568 wide;
- dish 1558641320 has cameras B and D only, so it has a 14 s clip and four stills. The others run
  30–50 s.

The photos are linked as ticket 08 says (`Tests/Eval/Sugary/photos` → `~/repos/sugary/eval/external/
photos`), and all 78 cases find every file. The armed command is unchanged.

**Decisions to know about.**
- **"540 lines" is the short side.** A portrait phone clip is sent 540×960, not 304×540. The
  standalone's clips were 960×540 landscape, and a model samples about one frame a second whatever
  the size.
- **The eval clips are longer than the app allows.** They are 30–50 s, and the server refuses over
  20 s. They were kept at the standalone's length so that the first pass compares like with like
  against 8.3 g, and they are sent as files, so they do not go through `ClipProcessor`. Video cases
  send no overhead photo, as the standalone's video arm did. An image-only model therefore gets the
  eight stills alone (the standalone's `stills8`: Gemini 9.2 g, Haiku 16.6 g), not photo plus
  stills.
- **New wording on the turn.** The frames sentence is not the standalone's. The text is written
  before the send knows the model, and an image-only model would otherwise read "a video is
  attached" beside eight pictures. One sentence; whether it changes anything is unmeasured.
- **Accepted formats are decided by ffmpeg, not by the label.** Any `clip` part is read, whatever
  its content type, because iOS says `video/quicktime` and some browsers say nothing. Only the
  QuickTime/MP4/3GP and Matroska/WebM demuxers and the `file` protocol are allowed. That covers what
  phones record and closes the playlist/concat tricks that make ffmpeg read other files. An AVI or
  MPEG-TS is refused as unreadable.
- **20 s plus half a second's grace**, then cut to 20, because a phone stops a few frames late. A
  WebM with no stated length (a browser's recorder) is measured by its last frame.
- **Size limits.** `MaxClipBytes` is 200 MB: 20 s of 4K60 HEVC is about 135 MB, and 1080p is 20–50
  MB. Only `/api/estimates` raises Kestrel's 30 MB body limit and the form's 128 MB part limit, to
  clip + 4 × 5 MB + 1 MB. **Ticket 12's reverse proxy must allow bodies that large too.**
- **Status codes.** 400 for every refusal of what she sent. 500 when the server cannot read clips at
  all (ffmpeg missing, or a run past `Clips:TimeoutSeconds`, 120): not her clip's fault, and not 503,
  which the app reads as "not set up".
- **`blurdetect` needs ffmpeg 6.0 or later.** The image and this machine have 6.1.1. On an older
  ffmpeg no frame is measured, so no still is taken, and every clip is refused as unreadable.
- **No client-side length check.** A 60 s clip is uploaded whole before the server refuses it, which
  costs her mobile data. Reading `duration` from a `<video>` element before sending would save that.
  It was left out to keep the control simple.
- **A clip-only meal has no thumbnail.** The estimating screen and the saved meal show no photo.
  After a reload the clip is gone, like photos, but `photosGone` does not fire, because there is no
  thumbnail. Her answers still work, because the server holds the clip. A 410 fallback would then
  re-estimate without the clip. Drawing a thumbnail from the clip's first frame would close both
  gaps.
- **WebChat review.** Superseded on 2026-10-15; see the amendment in ticket 11 and ADR 0046. The review
  topic now plays the clip, which WebChat stores up to 20 MiB, and no longer copies or shows its stills,
  so it shows what a video model saw.

**Not done / owed to the paid run.**
- Box 5's first comparison: Gemini 3.8 Flash on the 14 filmed against the 14 photographed `n5k`
  meals, against 8.3 g / 11.2 g. The "reports video and photo apart" half is built. The armed pass
  for this tier is now 78 meals: about $0.11 more than ticket 08's estimate (14 × ~$0.008).
- Still unverified against a real model (ticket 09's caveat): whether OpenRouter's Responses wire
  takes `input_video` with a data URL. The first armed pass on Gemini answers it. If it does not,
  every filmed meal fails loudly, and only `VideoInputParts` changes.
- Nothing was filmed with a real phone. Real clips' lengths and sizes, and whether `blurdetect`'s
  1.6× tolerance suits hand-held motion blur, are unmeasured.

### 2026-10-15: first paid runs, compared with the standalone figures

Gemini 3.8 Flash, one pass each (`.eval-output/scorecard-sugary-google-gemini-3.8-flash.json`, gitignored).

- **Pass 1, the agent still had file tools:** 6 of 78 meals got no answer; 2–6 model calls per meal; $0.020 per estimate; photo 12.0 g. Two scenarios failed on tool use. This led to the `noTools` change (`sugary` became a pure estimator; Jonas got `/sugary`).
- **Pass 2, no tools:** all 78 meals answered, all four tests green, $0.008 per estimate, median 15 s (the standalone took 23 s).

| Gemini 3.8 Flash | Pass 2 | Standalone |
|---|---|---|
| Photo, 64 meals: mean error | 12.8 g (bias −7.2) | 12.3 g (bias −8.3); about 13–14 g across models |
| Photo, 64 meals: truth inside range | 59% | 50% (before the server's range floor) |
| Nutrition5k, 14 dishes: video | 8.9 g (bias −1.0), 79% in range | 8.3 g (runs of 7.5–9.0) |
| Nutrition5k, 14 dishes: photo | 12.3 g (bias −6.7) | 11.2 g (runs of 9.9–12.2) |

Per set, from a photo: Nutrition5k 12.3 g, Nutrient Vision 9.7 g, SNAPMe 14.7 g.

Read: from a photo the agent is level with the standalone app, within the spread seen between its runs. The gain from video reproduces: 3.4 g here against 2.9 g in the standalone. That is one pass against three, so both are noisy.

Spend: pass 1 cost $1.82 and pass 2 $0.64, account figures.

### 2026-10-15: photos, the label photo and the clip taken in the app, on the back camera

**Why.** On her phone, "Take a photo" and "Film the plate" opened the front camera. Both were `<input capture="environment">`, which hands over to the phone's camera app, and many Android camera apps (Samsung's especially) ignore the facing hint and reopen whichever camera was used last. The live scanner's `facingMode: { ideal: "environment" }` is only a wish, which some phones also answer with the front camera. The user chose to capture in the app itself, always starting on the back camera. "From gallery" is unchanged.

**What.**
- `Sugary.Client/src/camera.ts` picks the camera, in this order: the back camera that worked earlier in this session (`deviceId: { exact }`); `facingMode: { exact: "environment" }`; a `videoinput` whose label says back (`back`, `rear`, `environment`, `trasera`, `posterior`; the plain one before ultra wide or tele), which works once labels can be read, i.e. after a camera has been allowed; `facingMode: { ideal: "environment" }`, and if that opened a camera that is not the back one while a back-labelled one now shows, the first is closed and the back one opened; then any camera (a computer). A refusal stops the chain at once. The camera found is remembered for the session, but only if it is not a front camera. A flip is never remembered, so the next photo starts on the back camera again.
- `components/Camera.tsx` has `CameraButton` and `CameraView`. The view is a full-screen `<dialog>` shown modal, so it sits above the packaged-food sheet too. It has a shutter or record button, close, and a flip button when there is more than one camera. The camera stops whenever the view closes, unmounts, or the app is hidden. A take still running when the app is hidden is thrown away.
- **Photos.** The picture on screen is drawn to a canvas at the stream's own size (asked for 4096×3072, 4:3) when the shutter is pressed. Where `ImageCapture` exists (Chrome on Android), the camera is then asked for a full photo. Its photo is used unless it fails, takes more than 4 s, or comes out the other way round from the picture on screen. Otherwise the canvas frame is used. Either goes to the same callback as a picked file: `forUpload` for meal photos, and `readBarcode` then `forUpload` for the label.
- **Clips.** `recording.ts`: `MediaRecorder` with the first supported type of `video/mp4;codecs=avc1`, `video/mp4`, `video/webm;codecs=vp9`, `vp8`, `video/webm`. No audio is asked for, so there is no microphone prompt. Settings are 1920×1080 at 5 Mbit/s (about 12 MB for 20 s), recorded in 1 s slices. An elapsed timer shows "0:07 / 0:20", and the take stops itself at 20 000 ms. The server's half second of grace covers the last frame. The file is `meal.mp4` / `meal.webm` / `meal.mov`, with the codecs left off its type, and is sent as the `clip` part exactly as a picked file was.
- **Scanner.** `BarcodeScanner` opens its camera with the same chain, at 1920×1080.
- **Fallback.** Some cases fall back to today's `<input capture="environment">`, with a note under the buttons (English and Spanish): no `getUserMedia` or not a secure context, no `MediaRecorder` (filming only), or the camera refused or failing in the app. After a refusal or failure the fallback lasts until the app is reopened, and the note asks her to tap again and check the camera app is on the back camera. The scanner keeps its own error messages.

**Tests.** vitest has 29 new tests (74 in all):
- the selection order and each fallback, against a fake `mediaDevices` whose labels are blank until a camera is allowed;
- label matching in English and Spanish;
- remembering, and forgetting a vanished or front camera;
- flipping;
- the mime choice;
- the 20 s auto-stop with fake timers;
- the stop, cancel and empty takes;
- file naming.

`tsc` and `npm run build` are clean. No .NET code changed. `SugaryAppServingTests` serves a stand-in web root, not the build.

**Checked in headless Chromium** with a fake camera (no phone):
- the photo goes through `ImageCapture` and arrives as `meal-0.jpg`;
- a 2.3 s take arrives as `clip`, `meal.mp4`, `video/mp4`, H.264 1920×1080 with a stated duration;
- no camera is left running after either;
- the label camera opens above the sheet;
- the scanner opens;
- with the camera refused, both buttons become file inputs and the note shows.

**Not verified.** Nothing has been tried on her phone yet. What a real Samsung does with `exact: "environment"`, whether its `takePhoto()` is upright and quick, and the recorder's real file sizes are unmeasured. A clip taken through the fallback still has no client-side length check.
