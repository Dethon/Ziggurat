# 02: npm build output arrives through the sandbox

Spec: `.scratch/app-spaces/spec.md` ("A built app is static files, however it was made").

**What to build:** In a household with a sandbox, the agent builds an npm project in `/sandbox` and copies its build output into `/apps/<slug>/` from the same command. The bytes arrive intact, text and binary alike, and publishing serves them. This proves the npm mode the user chose works over the existing FUSE bridge, with no new mechanism.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A sandbox command copying a tree of HTML, JS, CSS, a font, a PNG and a wasm file into `/apps/<slug>/dist/` lands every file byte-identical. Binary files go through the blob write that a copy streams through.
- [ ] A command that copies a `node_modules/` directory into `/apps` sees those writes refused in its `vfsChanges`, and nothing of it is stored.
- [ ] After `./publish` with `root: "dist"`, the served commit holds the copied tree.
- [ ] Covered by a sandbox E2E case in the existing sandbox collection's style, with the apps server hosted beside it.
