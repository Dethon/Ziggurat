# 0040 — A scenario stops at its threshold

Status: accepted
Date: 2026-09-10

## Context

A full tier is 228 runs over 73 scenarios, almost every one declaring two of three, and the last
pass was green on 222 of them. ADR-0032 launched a scenario's runs together and, as a consequence
it named, took every declared run of a passing scenario: with the third run already in flight
beside two greens there was nothing to stop, and a uniform denominator — every rate over N — is
what a scorecard diff reads best through.

That denominator costs a third of every green pass. On a suite that is green almost everywhere,
the run past the threshold is the single largest line on the bill, and it buys a number on a
scorecard nobody diffs on the edit-run-edit loop a prompt author is actually on. The scorecard
now says what each scenario spent (the `spend` row), so the size of the line is a fact.

## Decision

A scenario stops once k of its runs have passed. `RunPolicy`'s width defaults to k rather than
N: the first wave is exactly the runs a green scenario needs, a failed run is replaced by the
next declared one, and the launch loop counts runs in flight as green the way its unreachable
check already did — so a scenario never starts a run whose result could not change its outcome.

`Exhaustive` is the opt-out, on the policy and behind `ZIGGURAT_EVAL_EXHAUSTIVE=1` for a whole
pass: every declared run, together, rates over N. It is what a model-bump diff sets, because two
scorecards are only comparable row by row when both rated every scenario over the same count.

## Consequences

A green pass takes k runs per scenario instead of N: two of three where it took three, the
saving a third of the bill and none of the flake tolerance — a scenario one run short of its
threshold still gets its remaining runs, and a red still spends up to N.

What is lost is the third run's information on a green scenario: a scenario passing two of two
may have failed the third, and that flakiness is now invisible until it lands in the first wave.
A scorecard's `runs` column says which denominator a row was rated over, so a 2/2 is never
mistaken for a 3/3. The parity loop, which reads those rates as a trend, runs exhaustive.

A red scenario is also slower than before: its first wave is k runs, and the replacement goes
out only once a failure has come back, so a red pass has two waves where it had one. That is
wall clock on a pass that is red anyway, and the early stop on an unreachable threshold from
ADR-0032 still holds in the same loop.

ADR-0032's decision stands — runs go out together, one gate bounds the stacks — and its
consequence "every rate on the scorecard is over N" is now true of an exhaustive pass alone.
