# 01 — The mount point resolves inside the container

**What to build:** A command written with the mount-prefixed spelling the filesystem prompt
teaches resolves against the sandbox, instead of failing with "no such file or directory". The
container-native spelling keeps working too, because the image makes them two names for one
place. The exec tool says so in its own description, along with the rule for reusing paths that
appear in command output.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] A command naming a file by its mount-prefixed path resolves it, in a built image.
- [x] The same file named by its container-native path still resolves, unchanged.
- [x] The alias is part of the sandbox image, so a deployment cannot come up without it.
- [x] A test fails if the sandbox filesystem's mount point stops matching the alias baked into
      the image.
- [ ] An end-to-end test against the compose stack proves the alias, since no in-process fixture
      builds the image and none can see it. *(`Tests/E2E/Sandbox/SandboxPathE2ETests.cs` is
      written and skips itself when Docker is absent; it has not been run yet, because the Docker
      daemon was unreachable on the machine this was implemented on.)*
- [x] The exec tool description states that either spelling works inside a command, and that
      paths appearing in command output are container-native and need the mount prefix in front
      of them before being used as a path argument.
- [x] Globbing the mount is unchanged: the alias is not traversed during enumeration, and
      containment checks still terminate on it.
