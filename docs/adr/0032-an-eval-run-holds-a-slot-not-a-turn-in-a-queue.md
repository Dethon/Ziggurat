# 0032 — An eval run holds a slot, not a turn in a queue

Status: accepted
Date: 2026-08-20

## Context

A full tier is 189 runs: sixty scenarios, each declaring two of three or two of four. Every run
stands up its own stack — seven MCP servers in process, a sandbox container, a vault on disk — and
then waits on a real model, so a run is tens of seconds of which almost all is waiting.

Only one thing put runs in flight together: xUnit parallelizes across collections, and a class is
one collection, so the ten family classes ran ten scenarios at once. Inside a class the scenarios
were serial and inside a scenario the k of N runs were serial, which made the wall clock the
*longest family* rather than the work. `VaultScenarios` held eleven scenarios at three runs each,
so a pass that had 189 runs to do took 33 runs' worth of time and never had more than about six
runs going. Adding scenarios to an existing family made it worse in a way that adding a family did
not — a shape nobody chose.

## Decision

Runs of one scenario go out together, and one gate bounds how many stacks exist at once.

`RunPolicy` gains a **width**, defaulting to N: the k of N runs are launched up to that many at a
time, and the decision to launch stays serial, so a threshold that has become unreachable still
stops the runs nobody has started. `EvalConcurrency.Gate` is a process-wide semaphore around the
stack — `ZIGGURAT_EVAL_PARALLELISM`, defaulting to half the machine's cores between four and
twelve — so the two independent sources of parallelism cannot multiply into thirty containers and
thirty concurrent requests at one provider account.

Two things had to stop being per class to make that safe. A run **leases its own Redis database**:
the scheduling server clears fixed key names on the way in, which is correct for a run that owns
the database and destructive for a sibling that is mid-turn. And the scenario classes are capped
at six scenarios, sharding the long families round-robin, because a class is a serial chain and
the pass is only as short as its longest chain however wide the gate is opened. A test holds the
cap rather than a comment.

## Consequences

What this costs is the early stop, in exactly one case: a scenario whose threshold has become
unreachable has its remaining runs already going, so a hard failure now spends up to N where a
serial pass spent N-K+1. That is at most a run or two, on a pass that is already red, and it buys
back a uniform denominator — every rate on the scorecard is over N — which is what a scorecard is
read as a diff for. A scenario that would rather have the saving narrows its own width, and the
run policy at width one is the old behaviour exactly.

Which run explains a failure can no longer be "the first one to come back", because that is now a
race. Every run carries its number, and the dump, the route and the failure list are read back in
that order: the same red pass reports the same run.

Nothing about ADR-0030 changes. The suite still takes every declared run of a passing scenario,
still never retries until green, and still reports its rate over the runs it actually took.

Measured on the timer family — five scenarios, fifteen runs, armed against the shipped model:
**2m14s before, 49s after**, both green and both over the same fifteen runs. A family is the
smallest thing worth measuring and also the least favourable one, since a single class cannot use
more of the gate than one scenario's width; the full tier gains again from the sharding, where the
longest chain drops from eleven scenarios to six.
