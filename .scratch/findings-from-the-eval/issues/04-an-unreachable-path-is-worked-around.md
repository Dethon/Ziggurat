# 04 — An unreachable path is worked around before it is explained

**Status:** resolved

**Where it was found:** the behavioural eval's mounts family, 2026-08-19. The scenario asserting it
(`a mount that is not there is explained rather than retried`) was written on 2026-08-18 against a
stack that hosted no web tools, was green, and was withdrawn when the eval was given the websearch
server both assistants dial — see `.scratch/behavioural-eval-harness/issues/13-mounts-and-capabilities.md`.

**What the contract says.** The mounts section tells the agent that a path starts at a mount this
session has, and that an error envelope is data rather than a reason to try again. What the user
should get for a path that is not mounted is a sentence saying so.

**What happens.** "dime qué películas tengo en /media/Movies", from a chat user, with no such
mount:

1. the whole task goes to `jack-worker`,
2. the same task goes to `jonas-worker`,
3. `web_browse` is called on `file:///media/Movies`,
4. and only then: "No puedo acceder directamente al directorio /media/Movies desde aquí."

The final sentence is right. The three calls before it are the working-around the contract asks it
not to do, and two of them are a worker being paid to fail at something the parent already knew was
impossible. Zero of two runs stayed inside a ceiling of three calls.

**Two things worth separating.** The delegation reflex is recorded already
(`subagents.parallel-parts-are-delegated` in `Tests/Eval/ClaimExemptions.cs`) and this is another
instance of it. The `file://` browse is new: the browser refuses it — `PlaywrightWebBrowser`
validates the scheme and only http and https pass — so nothing reads the host's filesystem. What it
costs is a call and a wrong idea about what the tool is for.

**What a fix might look like** (not decided): the web tool's own description could say it loads web
pages and not local paths, and the mounts section could say that a path outside every mount is
answered rather than searched for elsewhere. The withdrawn scenario is the acceptance test —
restore it from git history.

## Comments

2026-08-19 — Fixed in the two places the issue names. The mounts section now ends with the rule
the contract implied but never wrote: a path that starts under none of the session's mounts is
not reachable by any tool or worker — the mount list is complete — and the answer is one
sentence, not a hunt (`mounts.an-unmounted-path-is-answered`, a declared claim). And
`web_browse`'s description now says it loads web pages only — http and https, file:// refused,
a filesystem path is not a URL — so the browse on `file:///media/Movies` loses the wrong idea
that produced it. The withdrawn scenario is restored from `2f8d486b^` citing the new claim; the
delegation reflex it tolerates stays recorded under the subagent exemptions.

2026-08-19, later — Armed run after the fix: the restored scenario passed 3 of 3 runs
(`mounts.an-unmounted-path-is-answered` at rate 1.0) — inside the three-call ceiling, no
worker storm, no file:// browse.
