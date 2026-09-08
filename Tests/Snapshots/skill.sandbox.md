# sandbox — description 112 / 130 tokens, body 927 / 1200 tokens, served by mcp-sandbox

================================================================================================

---
name: sandbox
description: Running anything in the Linux sandbox — a command, a script, a checksum, a pip install, a git clone — or keeping working files in its persistent workspace ("compute the sha256 of that file", "run this python", "clone the repo and count the lines"). Not for reading or writing files on another mount. The layout, what persists, what is preinstalled, how exit codes, output caps and timeouts come back, and how a container path maps to a virtual one.
---

### Layout

- `/sandbox` — the container root (`/`), for every tool including command execution. The container knows this name too, so `/sandbox/etc/os-release` and `/etc/os-release` are the same file whether you write one as a path argument or inside a command.
- `/sandbox/home/sandbox_user` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. Nothing puts you here by default: name it as the working directory when you want to work in it.
- `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as an unprivileged user) — the container root included, so a command that writes a relative file needs a working directory you own.

### Capabilities

- **File operations.** Standard read/write/glob/search/move/remove are all available. Scope them deliberately: this mount's root is the whole container, so a recursive glob or search starting at `/sandbox` is a walk over every path in the image. Both stop at a budget and say so, but the answer you get back covers whatever they reached before stopping. Start from the directory you mean — `/sandbox/home/sandbox_user` for your own files.
- **Command execution.** Commands run via `bash -lc` inside the container. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files written to the persistent workspace do. See the exec tool's description for argument details, the working directory, and limits.
- **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
- **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

### Behaviour you should rely on

- **Exit codes** are returned as data, not raised as errors — branch on them.
- **Output is capped** per stream and the result flags truncation; for long output, redirect to a file and read it back with the file tools.
- **Timeouts** kill the entire process tree and surface a timeout flag; raise the limit only when you genuinely need a longer-running command.
- **The persistent workspace is the only place that is both writable and durable.** Most paths outside it refuse writes with permission denied, because you run as an unprivileged user — but world-writable locations like `/sandbox/tmp` accept them and are wiped when the container is recreated. Keep working files under `/sandbox/home/sandbox_user/...` and set that as the working directory when a command writes relative files.
- **Paths in command output are container-native.** `pwd`, `find`, `which` and the rest answer in the container's own spelling (`/home/sandbox_user/x`, without the mount point). Put `/sandbox` in front of one before handing it to a filesystem tool as a path — inside another command it works as it stands. The `cwd` the exec tool reports is the exception: it already comes back as a virtual path.

### Working here

Edit files under `/sandbox/home/sandbox_user/...` with the filesystem write tools, then run them with the exec tool. Both operate on the same volume.

- **Persist results, not steps.** Keep working files in the persistent workspace. When the user wants the *result* somewhere durable, write a clean summary onto the mount that holds it — don't dump raw command output there.
- **Be honest about what you ran.** Never claim you ran a command you didn't, and say so when one failed.
