# Prompt skills

Status: ready-for-agent

Grilled 2026-09-08. Glossary: `CONTEXT.md` § Prompt (**Base prompt**, **Skill**, **Trigger
claim**) and § Home Assistant (**Setup index**). Decision:
`docs/adr/0039-a-skill-is-loaded-by-the-model-and-shipped-by-the-server-whose-tools-it-teaches.md`.

## Problem Statement

Every turn Francisco has with Jonas or Nabu starts with the whole manual. Before Nabu hears
"what time is it" it has read about 14,300 tokens: how an Obsidian note is laid out, what the
sandbox refuses, how to read a Home Assistant history file, how to phrase a watch, how a
scheduled task is filed. Almost none of it is used by almost any turn. It costs money on every
turn, it costs a full cache miss on every scheduled fire, and it puts a hundred rules in front of
the model when the twenty that matter for this turn would be followed more reliably. The Home
Assistant guide alone is a third of the prompt and grows with the house, not with an edit.

Authoring suffers too. Guidance for a server's tools lives in one file the agent host reads in
full, so a change to how printing works is a change to the same prompt every agent shares, and
nothing ties a piece of guidance to the server whose tools it explains.

## Solution

The base prompt keeps only what the model needs *before* it decides what to do: who it is, how
it speaks on this channel, when to delegate, what to remember, which mounts exist, and the rules
that choose between mechanisms ("a duration under four hours is a timer, never a calendar
alarm"). Everything it needs only *while* doing a task — the doing rules for the vault, the web,
the sandbox, scheduling, timers and the home — becomes a **skill**: a body the model loads with
one tool call when a request calls for it. Only each skill's name and one-line description sit in
the base prompt. The framework the agent already runs on provides the loading.

A skill is shipped by the tool server whose tools it teaches, as standard resources beside its
tools, and read at warmup the way the server's prompt is read today. It is declared in the same
manifest as every section, with budgets, claims and a snapshot, so the tests that keep the prompt
honest keep the skills honest. Its description is a claim the eval tests: a request of that kind
must load it.

The one live part of the prompt, the Home Assistant setup index, stops being prose and becomes a
file the mount serves, built when it is read. The base prompt's stub says to load the home skill
and read the file in the same turn. Nothing else in a skill changes while a conversation lives.

Sections move one at a time, largest first, each gated by a full-tier eval pass before and
after. Equal behaviour with fewer standing tokens is worth merging; a regression is not.

## User Stories

1. As a person talking to Nabu, I want a turn that has nothing to do with the home or the vault to cost no tokens for the home or vault guides, so that ordinary turns are cheaper.
2. As a person talking to Jonas, I want a request about my vault to be answered with the same care as today, so that moving the vault guide behind a load changes nothing I can notice.
3. As a person, I want the agent to still choose the right mechanism — a timer for "in twenty minutes", an alarm for "at seven", a watch for "when the window opens" — without loading anything, so that the choosing rules never depend on a load.
4. As a person on a voice satellite, I want a home request to be answered with at most one extra beat, so that a skill load is not something I hear.
5. As a person, I want the agent to read back the same home entity names as today, so that the setup index moving into a file does not change what it knows about my house.
6. As a person, I want a device I added to Home Assistant this morning to be known to the agent this afternoon, in a conversation that started last week, so that the index is never staler than the task.
7. As a person, I want the agent to load the home skill and read the setup index in the same turn, so that the split costs one round trip and not two.
8. As a person, I want a watch request to load the watches skill, so that the watch rules are as complete as today when they are needed and absent when they are not.
9. As a person on Telegram, I want a scheduled task's fire to be cheaper than today, so that the cache miss each fire pays is over a smaller prompt.
10. As the maintainer, I want every skill declared in the same manifest as every section, so that one table says what may reach the model and what it may cost.
11. As the maintainer, I want a skill body to have a budget and a snapshot of its own, so that a body that grows is a diff somebody reads, not a silent cost.
12. As the maintainer, I want the agent's prompt snapshot to show the advertised skill list exactly as the model sees it, so that the snapshot is still the whole prompt.
13. As the maintainer, I want the base prompt's ceiling to come down after each section moves, so that the standing prompt cannot grow back to where it was.
14. As the maintainer, I want a section under about five hundred tokens to stay in the base prompt, so that the advertisement and the load hop are never paid for less than they shed.
15. As the maintainer, I want a skill's description to be a declared claim cited by its family's scenarios, so that a skill nobody loads shows as a red description.
16. As the maintainer, I want the scorecard to say whether a red scenario failed to load the skill or loaded it and ignored the rule, so that I know whether to edit the description or the body.
17. As the maintainer, I want a load to be an ordinary call in a scenario's permitted set, so that loading the wrong skill is visible as a wrong choice.
18. As the maintainer, I want the demonstrated-red procedure written down for skill claims — body prose deleted, description intact — so that a citation on a skill claim means what a citation on a section claim means.
19. As the maintainer, I want the staleness tests to walk skills the way they walk prompts — every skill a server serves is declared, every declaration is served, every path a body teaches starts at a real mount — so that a skill cannot drift from the code it describes.
20. As the maintainer, I want a server that omits its skill resources to fail the server contract tests, so that shipping tools without their skill is a build failure.
21. As the maintainer, I want the tool server to own the skill for its tools, so that a deployment without the printer server cannot advertise printing guidance.
22. As the maintainer, I want the agent definition to grant skills by granting servers, so that there is no second list to keep in step.
23. As the maintainer, I want a subagent to get skills with the servers it is allowed, so that a worker is never more or less taught than its tools warrant.
24. As the maintainer, I want an outpost that publishes a skill to be treated as an outpost that publishes a prompt is today — bound under the undeclared budget and reported — so that outposts keep one rule.
25. As the maintainer, I want the skill text to live in code beside its name constant, like every section, so that prose stays reviewable in a diff and reachable from a test.
26. As the maintainer, I want the framework's load tool to need no approval, so that a load never stalls a turn on a prompt nobody sees.
27. As the maintainer, I want a loaded body to stay in the conversation and never be reloaded, so that a long conversation pays for a skill once.
28. As the maintainer, I want the servers to publish skills in the standard shape, so that switching to the framework's own discovery later is a one-file change.
29. As the maintainer, I want the alpha discovery package kept out of the deployment, so that a prerelease dependency does not sit under every turn.
30. As the maintainer, I want the eval to run on fresh conversations with the deployed prose, so that what it measures is what was shipped.
31. As the maintainer, I want each move to be one branch with a scorecard before and after, so that a regression is attributable to one section.
32. As the maintainer, I want the file that holds the core directive to carry its section's name, so that "base prompt" can mean the standing whole without the code contradicting it.
33. As the maintainer, I want the prompts rule file to state the timing rule and the size floor, so that the next section added lands on the right side without a discussion.
34. As the maintainer, I want the loaded skill's tool call to appear in the eval recording like any other call, so that scenarios can require or permit it with the vocabulary they already have.

## Implementation Decisions

### The dividing line

A rule the model needs before it decides what to do stays in the base prompt; a rule it needs
only while doing the thing becomes a skill. A section with both splits into a standing stub and a
skill body. The stub carries the choosing rules, their claims, and one sentence naming the skill
and anything to read alongside it. A section under about five hundred tokens stays whole.
Persona, identity, user context, delegation, memory, the mounts list, date, voice, language,
printing and Idealista stay. Jack's downloader persona is untouched.

### The manifest

A skill is a declaration in the same table as the sections: a name, a one-line description that
is the advertisement, a description budget, a body budget, the server that serves it, and its
claims. The manifest aggregates skill claims with section claims so claim coverage sees one set.
The advertisement's explanatory prose is a declared section at feature priority carrying the
skills' trigger claims; the framework's instruction template is cut to the bare list it appends
at the tail. Each agent's snapshot is rendered through the framework's provider so it shows the
list; each skill has its own snapshot of its body. The per-agent ceiling drops after each move to
what remains plus a fixed headroom.

The file holding the core directive is renamed to its section name, so "base prompt" names the
standing whole.

### Transport and ownership

A tool server publishes each of its skills as resources in the standard shape: one index document
listing name, type, description and body location, and one body resource per skill whose text is
the skill's markdown with a name-and-description frontmatter. A hosting helper builds both from
the skill constants in the domain, the way the filesystem resource helper builds a mount's
resource, so no server hand-writes a resource. The skill text and its name constant live in the
domain beside the sections, as prose in code.

The client manager reads the index and bodies at warmup from every dialled server that has them,
through the same short-lived cache the prompts use, and binds each by name to its declaration. An
undeclared skill binds under the default budget with a warning, as an undeclared prompt does.
Outposts go through this path unchanged. The session exposes the bound skills beside its prompts.

The agent attaches the framework's skills provider with the session's skills as inline skills.
The load tool is auto-approved. Resource and script tools are not advertised because no skill
ships resources or scripts. The provider's template contains only the list placeholder.

Because the load tool rides the same tool pipeline as every other tool, the eval's recording sees
it under its own name, without a server prefix.

### The setup index

The setup index becomes a file at the root of the home mount, built on every read from the same
catalog, watches and satellite sources that build it today. It leaves the served prompt. The
home stub says: before a home task, load the home skill and read the file. The home skill body
says to re-read it when a name does not resolve.

### Eval

A skill's description is a trigger claim, declared beside it and cited by every scenario of its
family. Scenarios require the load where the claim is cited and permit it elsewhere; a load is
never tolerated by default. The scorecard's per-scenario failure names whether a required load was
missing or the load happened and a checked behaviour did not. Red for a body claim is demonstrated
with the body's prose deleted and the description intact; the prompts rule file records this.

### Rollout

One section per branch, largest first: Home Assistant — the watches skill first as the tracer
bullet that proves every layer, then the home guide — followed by vault, web browsing, scheduling,
sandbox, timers. A full-tier eval pass runs before and after each move,
the scorecard is backed up before any family-filtered run, and the branch merges only if no claim
rate falls. Each move ratchets the ceiling.

## Testing Decisions

A good test reads what the model would see or what the server would serve, never how it was
assembled. Four existing seams, no new ones:

- **Prompt fixture and snapshots.** The fixture that assembles a configured agent's prompt from
  the shipped settings without a deployment gains the served skills, renders the advertisement
  through the real provider, and writes one snapshot per agent and one per skill. Budget tests
  cover description and body budgets and the ratcheted ceiling. Staleness tests walk skills in
  both directions and check every path a body teaches. Claim coverage runs over the union.
- **In-process MCP servers.** The existing loopback host and per-server fixtures prove a server
  lists its index and serves each body with the declared name and description, and that a session
  built against real fixtures, as the outpost mounting tests build one, exposes those skills. The
  server contract table gains a row per skill so a server that stops serving one fails the
  contract test.
- **Home Assistant fake catalog.** The existing file system tests with the fake client read the
  setup index file and see the same rooms, entities, actions, watches and satellite line the
  served prompt carried, and see a new entity on the next read.
- **Behavioural eval.** Existing scenario families cite the trigger claims and require the load
  through the existing call-expectation vocabulary. The scorecard's failure kind is asserted by
  the harness's deterministic tests, which run unarmed.

Prior art: the scheduling server prompt test that asserts a served prompt grew a generated
section; the library server test that lists a filesystem resource; the timer, watch and Home
Assistant scenario families; the claim coverage tests.

## Out of Scope

- Host-driven or channel-driven pre-loading of skills, including for voice.
- Skill resources and scripts; the framework's read and run tools stay unadvertised.
- The framework's alpha MCP skills discovery package.
- Reload-on-change of a loaded body; a conversation spanning a deploy keeps its body.
- Jack's downloader prompt, printing and Idealista.
- Any change to the retention of conversations or to message truncation.
- Any change to what tool descriptions say.

## Further Notes

The delegation experiment of 2026-08-20, where the when-to-delegate rules were moved onto the
subagent tool's description and measured as a coin flip, is the reason every move is gated by
the eval rather than assumed. The summary builder's measured trade — about a second per round
trip against a twentieth of a millisecond per token — is the reason the setup index is a file and
not a listing, and the reason small sections stay standing.
