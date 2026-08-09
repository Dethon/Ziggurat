# 05 — Capability refusal

**What to build:** Attaching a file to a model that cannot read it fails in a way that costs the
person nothing. The composer knows what the model a turn would run on accepts — the per-message
model choice where there is one, otherwise the agent's default — and when it cannot take an
attached kind the send is blocked with an explanation naming the model. The files stay attached,
so the fix is switching model rather than starting the message again. Switching to a capable
model unblocks the send with no further action.

The channel server refuses the same case before emitting anything, for the race where the model
changes between picking a file and sending it. Nothing reaches an agent and no turn is created;
the refusal comes back on the same stream-error path an undeliverable message already uses, so
every browser watching the topic sees the same end. The agent does not check a third time.

Two of the three models on offer today are text-only, so this is the common path rather than an
edge case.

**Blocked by:** 02 — Attachment capability discovery; 04 — Composer attachments.

**Status:** resolved

- [x] The composer resolves the effective model as the per-message choice, falling back to the agent default.
- [x] Send is blocked when that model cannot take an attached kind, with a message naming the model and the reason.
- [x] The attached files survive the refusal and survive switching model.
- [x] Switching to a capable model re-enables sending without re-attaching.
- [x] The channel server refuses an incapable model before emitting, creating no turn and waking no agent.
- [x] The refusal reaches every browser on the topic through the existing stream-error path.
- [x] Client tests cover the block and the files surviving it; hub tests cover the server-side refusal and its shape.
