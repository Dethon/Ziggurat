# 01 — Attachments land in the workspace

**What to build:** A file someone sends arrives in the agent's sandbox as a real file, in a
directory the container user can write, and it is still there after the container restarts. The
mount says where that directory is instead of the landing code guessing, so the same fix works
for any future mount that can execute.

Today nothing lands at all. Landing targets a directory at the container root, which the image
never creates and the unprivileged container user cannot write to; the write fails, the failure
is only logged, and the turn continues as if no file had been sent. Confirmed on the running
stack: the sandbox container runs as uid 1000, its root is owned by root, and the target
directory does not exist.

This restores what ADR-0021 already decided — the target it names is the workspace — and
ADR-0025 records what a mount now has to publish for that to hold.

**Blocked by:** None within this feature. The feature as a whole is sequenced after
`sandbox-path-consistency` 01-03, which rework the exec and home-directory semantics and
introduce the sandbox E2E stack this reuses.

**Status:** ready-for-agent

- [x] An attachment lands under the sandbox's declared workspace and is still there after the
      container is restarted.
- [x] The mount record carries a nullable workspace, declared by the backend, published in the
      `filesystem://` resource in backend coordinates, and read by discovery.
- [x] The landing directory is composed through the one mount-point translation, not by the
      landing code joining a mount point itself.
- [x] The sandbox declares its workspace from its existing home-directory setting, so the
      setting, the declaration and the landing target cannot disagree.
- [x] The layout is unchanged: a directory per conversation, one per message, the person's own
      filename kept, and a second same-named file in one message separated one level further
      down rather than renamed.
- [x] An exec-capable mount that declares no workspace lands nothing, and every mount that
      declares none behaves exactly as it does today.
- [x] The path reported back to the agent resolves through the filesystem tools.
- [x] The landing unit seam asserts the target sits under the declared workspace, with the
      existing collision and virtual-path cases unchanged.
- [x] The filesystem server conformance theory records which mounts declare a workspace — the
      sandbox alone — and what the published resource carries.
- [x] An end-to-end test against the compose stack writes a file as the unprivileged user and
      reads it back. It is the only layer where the image, the volume and the user are real; the
      in-process fixture builds against a temporary root the test user owns and cannot exercise
      a permissions fact at all.
