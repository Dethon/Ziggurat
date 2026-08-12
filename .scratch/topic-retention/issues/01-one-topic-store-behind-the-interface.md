# 01 — One topic store behind the interface

**What to build:** Topic storage has a single implementation. The chat channel and the agent
read and write topics and history through the same interface, instead of through two classes
that duplicate the key scheme and are kept in step by hand. Nothing a user can see changes.

This is prefactoring for everything that follows: the topic index must have exactly one writer,
and the hub's dependency on a concrete channel-local class is currently why no unit test can
exercise a hub topic method at all.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] One implementation of the topic and history key scheme remains; the channel-local
      duplicate is deleted.
- [x] The hub depends on the topic store interface rather than on a concrete class.
- [x] Every test that covered either implementation still passes, folded onto the surviving one.
- [x] A hub topic method can be exercised in a unit test without a container.
- [x] No observable behaviour changes.
