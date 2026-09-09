# glm-5.3-flash vs gpt-5.6-luna — what the gap turned out to be

## Where it stands

| | start of the work | after round one | after round two |
|---|---|---|---|
| z-ai/glm-5.3-flash | 0.728 | 0.873 (4 runs pooled) | **0.934** (5 runs pooled, r23–r27; single runs 0.912–0.947) |
| openai/gpt-5.6-luna | 0.974 | 0.987 | **0.985** (5 runs pooled, r11–r15; last run 0.996) |

Every run is 73 scenarios, 228 model turns, one provider pinned (`z-ai` and `openai`
respectively). Same code, same provider, single runs of glm sit in a band about four points
wide (r14–r17: 0.855 / 0.860 / 0.890 / 0.895), so nothing under two points between two
single runs means anything. Luna's band is narrower but not zero: 0.978 / 0.987 / 0.987 on
identical code.

**Parity is not reached.** The gap is about five points pooled (25 at the start, 11 after round one). The last glm run alone is 0.912 and the one before it 0.947, which is the width of the noise, so the pooled figure is the one to read.

## The one-line answer to the original question

You asked whether the system was overfit to luna. It was — but not by tuning. Luna was
silently correcting defects that were always there: wrong tool names, mounts that disagreed
with each other, error hints that sent a model in circles, fixtures that refused what they had
just handed out, and a harness that failed the very behaviour it tolerated. A model that reads
literally does what the text says, and the text was wrong. Most of the fixes make the system
more correct for any model, which is why luna's score went up too.

## Round one (16 commits) — the categories

**Tool names the model could not call** (4 commits): prompts said `file_read`, `exec`; the
registered names are `domain__filesystem__file_read` and so on. Luna mapped them; glm called
what the text said.

**Errors that named a symptom instead of a rule** (4 commits): shell operators, unknown
flags, a bare 500 from Music Assistant, "nothing is ringing". Each now says the rule.

**Rules stated in one direction only** (3 commits): the home scope rule, a watch's entity id,
the vault's "when not to search here".

**A safety gap** (1 commit): glm deleted seven of the user's notes unasked, 0/3 runs; now 3/3.
The `SkillDeclaration` schema gap behind it stays open by your decision.

**Fixture and harness bugs** (4 commits), including a run that timed out vanishing from the
scorecard.

## Round two — what glm was still tripping on, and what changed

The residual after round one looked diffuse: 114 failed runs over 38 scenarios. Reading the
dumps scenario by scenario, most of it was five mechanical things, none of them "glm is a
weaker model":

**1. The tool's own error sent the model round in circles.** A failed play told it to "list
the library (`browse_media.sh`)"; the bare browse failed with the same 500 and the same
advice — 13 execs on one station. The hint now depends on which action failed, names the one
listing call in full, and a failed listing says the listing failed. A 404 page was reported as
a `success` with no title and no content, so a model that had guessed one url guessed the next;
`web_browse` now carries the HTTP status and a `not_found` page says so. A ref from a page a
click had left was answered "browse that page again" — the second browse the back action
exists to avoid; it now says to go back. Buttons rendered as bare text, so a page whose
editions are reached by buttons read as a list of names and the model composed urls; they are
marked `[button: …]` now. The fake home answered a catalog search with the generic "ok,
nothing changed", indistinguishable from a call that never answers; it returns an empty result
list.

**2. Paths the model spelled almost right were refused, then accepted one call later.** A
schedule written to `/schedules/<agent>/<id>` instead of `…/schedule.json`; a timer likewise;
`ha/setup-index.md` without its slash. Each mount now accepts the only thing the spelling can
mean, and the create tool reports the file the backend actually wrote (echoing the caller's
directory back had luna "moving it into place" afterwards).

**3. Memory ids never reached the model.** `memory_forget` asks for the id "exactly as
spelled" and the recall block never spelled one, so glm invented ids and "success, nothing
affected" let the guess read as a deletion. The block now leads each fact with its id, the
prompt says so, an unknown id is a not-found naming where the real ids are, and `memoryIds`
says it deletes what it names — luna, given ids, listed the facts to *keep* in one call and
lost them.

**4. glm spoke the answer before the tool ran, and again after.** "Temporizador de nueve
minutos." twice, on every voice timer scenario. The voice rule said "say nothing first" as an
aside after the one-word-opener rule; it now leads with the default and keeps the opener as
the exception. A first attempt that named the echoed sentence as an example was copied.

**5. Descriptions that duplicated a skill, or commanded its load.** The timers mount carried
the whole timer.json shape, so glm wrote the file and never loaded the rules that go with it
(target, speaking room). Trimmed to a description that points at the skill — and a first cut
that *commanded* the load ("before you create, cancel or silence one…") had luna loading the
timers skill for a calendar snooze and a media restart. The watches rule led with "a new
threshold" as the edit case and glm edited the seeded 70 watch down to 50; it now leads with
the discriminator. The vault skill lists an append among its triggers.

**6. The fake hub said nothing was ringing.** The scenario said an alarm was, the dismiss
found nothing, the tool said "nothing is ringing", and glm went to the calendar and deleted
the event. The scenario now declares what rings and the hub reports it dismissed once.

**Harness strictness, fixed where the behaviour was already right:** one `ls` on the sandbox
for an unmounted path (the glob and file_info were already tolerated); a vault look for a
recipe named after a person; English forget queries and forget-by-id; "confirmo" among the
delete-question verbs; `satelliteIds: ["office-01"]` as a legal target spelling; the air
conditioner under either of the mount's two views; a threshold edit matched by its number; a
refused attempt corrected into the required call (the tool named the fix — that is the tool
working); a provider 520 keeps its row on the scorecard instead of dropping the scenario.

## Two measurement rules worth keeping

**Provider routing is pinned for evals** (`providerRouting.only`), in the committed
`Agent/appsettings.json`. Unpinned, glm spread from 0.728 to 0.911 across eight backends.

**Never build while an armed run is on.** The testhost maps `Tests/bin/Debug/net10.0` in
place; a rebuild under it taints or kills the run. Saved as a memory.

## What is left

After round two the dumps stopped showing mechanical causes. What fails glm now, run after
run, is one of four behaviours, each at about one run in three on a handful of scenarios:

- **A skill not loaded** for a vault write ("emptying a folder", "a new recipe"), or the
  wrong skill loaded (the timers skill for "why nine minutes?"). The descriptions are the
  trigger by design (ADR 0039); glm reads them and skips the load anyway.
- **A room guessed** when no speaking room exists (a text-channel timer request gets the
  kitchen), against a rule the skill states in bold.
- **A look in the vault before a web task** the user named as a web task ("en la web del
  Cuaderno de barrio"). Tolerated where the turn is ambiguous, still counted where it is not.
- **A sentence spoken before the tool runs** on voice turns, then the confirmation after it —
  down from every timer scenario to about one run in three, and luna does it too now and then.

None of these is a wrong tool name, a misleading error, or a fixture that refuses what it
handed out. They are glm following the prompt less reliably than luna does, and the two
levers left are the ones you would expect: raise `RunPolicy` on the contested scenarios so a
2/3 stops deciding the row, or accept the behaviours the harness still counts (the vault look
on an explicit web task is the one I would not relax). Both are contract decisions, not defects.

Provider glitches also show up as single failed runs: one r26 turn answered the Lisbon
message with a reply about a pizza timer, on the right input. The harness now keeps such a
run on the scorecard instead of dropping the scenario.

## Housekeeping you should know about

- The glm model switch and the z-ai pin are committed on `skills` (swept into `c704092b5`
  and `85da70611`). If the committed default should stay luna, that needs a deliberate revert.
- Scorecards for every run are in `.eval-output/scorecard-full.2026-09-09.<tag>.json`
  (glm r14–r23, luna r6–r11). Dumps for the last runs are beside them and in `flakes/`.
