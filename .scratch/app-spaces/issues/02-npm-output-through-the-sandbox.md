# 02: npm build output arrives through the sandbox

Spec: `.scratch/app-spaces/spec.md` ("A built app is static files, however it was made").

**What to build:** In a household with a sandbox, the agent builds an npm project in `/sandbox` and copies its build output into `/apps/<slug>/` from the same command. The bytes arrive intact, text and binary alike, and publishing serves them. This proves the npm mode the user chose works over the existing FUSE bridge, with no new mechanism.

**Blocked by:** 01

**Status:** ready-for-agent

- [x] A sandbox command copying a tree of HTML, JS, CSS, a font, a PNG and a wasm file into `/apps/<slug>/dist/` lands every file byte-identical. Binary files go through the blob write that a copy streams through.
- [x] A command that copies a `node_modules/` directory into `/apps` sees those writes refused in its `vfsChanges`, and nothing of it is stored.
- [x] After `./publish` with `root: "dist"`, the served commit holds the copied tree.
- [x] Covered by a sandbox E2E case in the existing sandbox collection's style, with the apps server hosted beside it.

## Comments

**Landed (branch `app-spaces`).** `Tests/E2E/Sandbox/SandboxAppBuildE2ETests.cs`, in the `SandboxE2E`
collection: the real sandbox image and FUSE daemon, the fixture's bridge, and the apps server beside it in
process (`AppsServer`, one per test, as the sibling classes host their mounts in the test process), mounted
the way the agent holds it — `McpFileSystemDiscovery` over a signed MCP client, so the operations the
bridge gates on are the ones the server published — with the call minted for the turn's person (the apps
server refuses nobody). No production code changed: every case passed on arrival.

- *Byte for byte*: a build left in the home (HTML; JS with CRLF, non-ASCII and no final newline; CSS; a
  woff2, a 300 KB PNG and a wasm, each holding NULs) copied with `cp -r` into `/apps/glucose/dist/`. Every
  file is one `create … applied` in the call's change log, and the working tree on the volume holds the
  exact bytes. Text arrives as the text create, the binaries as the blob write.
- *node_modules*: the whole project (`dist/`, `node_modules/` with a package's text and a native binary,
  `package.json`, `.gitignore`) copied in one command. Both dependency files — one per write path — are
  `refused` with `permission_denied`, the rest `applied`; no `node_modules` on the volume or in a glob.
- *Publish*: after the copy, `./publish` on the mount (a bare action skips the sandbox) with `root: "dist"`
  and a `.gitignore` naming `dist/`: the served commit holds every `dist/` file byte for byte (read from
  git), and the published origin serves each non-HTML file byte for byte; the page, served with the
  origin's tags put in, is held to its text.

**Red first, by breaking the code on purpose and restoring it:** truncating `WriteBlobAsync`'s bytes
(copy and publish cases red), normalising CRLF in the text create (copy and publish red), dropping the
`node_modules` refusal (node_modules case red: both files `applied`), and dropping `--force` from
`AppRepository`'s add (publish red: `dist/` not in the served commit).

**Run:** the class and its 53 collection siblings, 56/56 in 23 s wall (the three new cases add ~0.6 s);
`~Apps|~Conformance` 741 passed, 2 skipped; `FullyQualifiedName!~E2E` 11,222 passed, 224 skipped.

**Not covered:**
- *The sandbox image has no Node.* `McpServerSandbox/Dockerfile` installs Python, git, curl and DuckDB,
  but neither `node` nor `npm` (checked in the built `mcp-sandbox:latest`: `command -v node npm` finds neither), so "build the npm project in the sandbox" is not possible in the shipped
  image as it stands. The spec's problem statement says a sandbox household "can run `npm`". The test
  therefore places the build output in the home and proves the copy, which is what this ticket is about.
  Whether the image gets Node (and which pinned version), or the skill (ticket 12) tells the agent to
  fetch a Node tarball into its home, is a decision left to the maintainer.
- A bridge write carries the whole file as one chunk (`VfsCall.WriteBytesAsync`), so a sandbox command's
  binary is one `fs_blob_write` call whatever its size, up to `vfsBridge:maxFileBytes`. Only a copy tool
  call streams in ranged chunks.
