# 02 — The mount point means the container root in exec

**What to build:** Exec reads the mount point as the container root, exactly as glob, read,
search and info already do, so one mount point stops meaning two different directories
depending on which tool is called. The guidance the old default encoded — work in the
persistent workspace — moves into the prompt, where the agent can follow it as advice rather
than meet it as a rule that makes one tool disagree with the rest.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] Passing the mount point as the working directory runs the command at the container root.
- [x] Passing an empty path or a dot does the same, rather than redirecting to a home directory.
- [x] An absolute working-directory path is resolved under the configured container root instead
      of being taken verbatim, so a working directory cannot leave the mount.
- [x] A working directory that does not exist still returns a not-found envelope rather than
      throwing.
- [x] The home-directory setting no longer exists in the command runner's options, and the
      runner no longer reads it. The sandbox server's own setting and its appsettings entry
      stay: the original argument for deleting them — that the value only fed prose — stops
      holding, because `attachment-landing-target` 01 makes it the workspace the mount declares
      (ADR-0025). Deleting it here and restoring it there would be one commit undoing another.
- [x] The sandbox prompt states that the mount point is the container root, names the persistent
      workspace beneath it, and says writes elsewhere fail because the container runs as an
      unprivileged user.
- [x] The exec tool description no longer claims the mount point defaults to a home directory.
- [x] The existing sandbox integration tests pass unchanged, including the one that passes an
      empty working directory.
