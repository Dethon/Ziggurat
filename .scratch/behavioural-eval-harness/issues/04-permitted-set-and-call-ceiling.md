# 04 — Permitted set and call ceiling

**What to build:** "it did nothing unnecessary" becomes something a test decides.

A scenario declares its required calls (tool plus argument and path matchers) and its
**permitted set** (tool plus path pattern, no argument matching). Any call in neither fails the
scenario. A forbidden list was rejected in ADR-0030's design: it cannot fail when a newly added
tool starts being called for no reason.

A scenario also declares a ceiling on total tool calls. A model that flails through most of the
allowed iterations and then answers correctly is not passing.

**Blocked by:** 02.

**Status:** ready-for-agent

- [x] A call outside required-plus-permitted fails the scenario, naming the offending call.
- [x] A permitted call with unexpected arguments does not fail, since permission is by tool and
      path only.
- [x] A missing required call fails, naming what was expected and what was seen.
- [x] Exceeding the declared ceiling fails, and the recording is truncated at no point before it.
- [x] The first timer scenario declares both, and still passes.
- [x] Both checks are proven to fail correctly against a scripted chat client.
