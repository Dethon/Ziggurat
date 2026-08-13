# 03 — The sandbox stops asserting its own layout

**What to build:** The sandbox prompt names the mount point and the workspace it was given
rather than spelling both out as literals, so the directory the agent is told to work in is the
directory that exists. The exec tool's description stops naming any one mount's directories at
all.

The prompt currently spells the mount point eight times and the workspace four. The guard test
the path-consistency work adds pins the mount point to the image alias, not to this prose, so a
rename would leave the prompt quietly wrong while every test stayed green. The exec tool's
description repeats the same workspace path twice more, and it is a compile-time constant that
cannot interpolate — but it serves every exec-capable mount, so naming one mount's home
directory there is what created the duplication in the first place.

**Blocked by:** 01 — the prompt is built from the workspace the mount declares, which does not
exist until then.

**Status:** ready-for-agent

- [x] The sandbox prompt names the mount point and workspace it was given, with neither spelled
      as a literal anywhere in it.
- [x] Changing the sandbox's workspace setting or renaming the filesystem changes the prompt with
      no other edit.
- [x] The exec tool's description names no single mount's directory layout, and the guidance it
      used to carry about the sandbox lives in the sandbox prompt.
- [x] The filesystem server conformance theory asserts the sandbox prompt names the values the
      server was configured with. This is the only place a prompt is asserted anywhere, and it
      earns that here because the prompt has stopped being a constant.
