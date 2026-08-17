# 01 — A spawn carries the parent's context as one value

**What to build:** Nothing anybody can see. Spawning a subagent already hands down four things
that are all "what the parent was" — the conversation it belongs to, the person it answers for,
the tool patterns already approved for it — and the next thing to hand down is whether the parent
may see outposts. Rather than a fifth positional argument on a contract that is already five long,
the parent's contribution travels as one value.

The opt-in field exists after this ticket and nothing reads it. The behaviour of every agent and
every subagent is unchanged, and the suite is green on the same assertions it passes today.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A spawn context record in the agent domain namespace, beside the agent key and the disposable
      agent, carrying the conversation id, the user id, the whitelist patterns and the parent's
      outposts opt-in. Not in the settings-binding namespace: nothing binds it from configuration.
- [ ] The agent factory contract's subagent entry point takes the definition, the approval handler
      and that record, replacing the four positional parameters.
- [ ] The multi-agent factory composes the record from the spec it is already building against, so
      the parent's values reach the projection with no new lookup and no second source of truth.
- [ ] The projection's subagent entry point reads the conversation, user and whitelist patterns off
      the record. The opt-in stays unread; the hardcoded false it will replace is untouched here.
- [ ] The fake agent factory used by the integration fixtures follows the contract.
- [ ] The subagent tool and its feature config are unchanged. The spawn delegate stays "a definition
      in, a running agent out" — the parent's contribution is captured, as the conversation and
      whitelist already are.
- [ ] Existing projection and subagent tests pass unmodified except where they construct the new
      record, and no test asserts a new behaviour: this ticket adds none.
