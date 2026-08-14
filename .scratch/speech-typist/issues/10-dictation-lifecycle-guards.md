# 10 — Dictation lifecycle guards

**What to build:** the three ways a dictation can go wrong that have nothing to do with Whisper.

**The target window went away.** The window in front is identified when the binding goes down, and
that window is the only one the dictation's words are allowed to arrive in. Before each injection the
current window is compared against it; if it differs — you let go and clicked into something else
while a segment was still being transcribed — the remaining segments of that dictation are discarded
and you are told once. Words in the wrong window are worse than missing words: the tail of a sentence
landing in a terminal, a chat box or a game is the failure this exists to prevent.

**The key-up never arrived.** A hook can lose a key-up to a remote desktop session, fast user
switching, or a lost window. Without a guard the microphone stays open for the life of the process. A
dictation therefore ends by itself after a bounded time and the capture device is closed. This is a
correctness requirement, not a nicety.

**A second binding during a live dictation.** Ignored. Two languages must never interleave into the
same window, and the first dictation carries on undisturbed.

Because segments are injected while the binding is still held, injection releases any modifier the
binding itself holds down for the duration of the call and restores it afterwards. The shipped
default binding has no modifier precisely so that this path is rarely exercised, but a binding with
one must work.

**Blocked by:** 09 (reuses its notification path and tray error state).

**Status:** ready-for-agent

- [ ] The window in front is identified when the binding goes down
- [ ] A changed window discards the remaining segments and injects nothing into the new one
- [ ] That discard notifies once, not once per remaining segment
- [ ] A dictation whose key-up never arrives ends by itself and closes the capture device
- [ ] A second binding pressed during a live dictation is ignored and the first continues
- [ ] Injection with a modifier-carrying binding held releases and restores that modifier
- [ ] All of the above is covered through the fake host, including the timeout, with no Windows
