# 14 — Check FUSE under prod's AppArmor

**What to build:** A person runs the sandbox image on the prod host with the FUSE device and capability, mounts through the launcher as 07 does, and runs a mode-`0111` ELF from the mount. If AppArmor refuses the mount, they add the narrowest security option that works (a profile allowing the FUSE mount before unconfined) to the deployment. The spike branch's probe script is a ready test. Prod access: ask the person if it isn't already set up.

**Blocked by:** 07 — The shell reads every mount

**Status:** ready-for-human

- [x] The mount succeeds on prod, or the narrowest working security option is added and recorded on this ticket
- [x] A mode-`0111` ELF served through FUSE executes on the prod kernel

## Comments

**2026-10-01, before this is picked up — it now gates every sandbox command, not only FUSE.** Since ticket 06 the launcher puts *every* exec in a private mount namespace (`unshare(CLONE_NEWNS)` + `mount(MS_REC|MS_PRIVATE)`), and since 07 it also mounts a tmpfs at `/run/vfs` and the FUSE daemon mounts `/vfs`. Docker's default AppArmor profile (`docker-default`) denies `mount`, so on an AppArmor host the first symptom would be every `exec` answering `cannot isolate the command: Permission denied`, not merely a missing `/vfs`. Check this before deploying the branch.

What the deployment now asks of the sandbox service (`DockerCompose/docker-compose.yml`): no `user:`, `cap_add: [SYS_ADMIN]`, `devices: [/dev/dri, /dev/fuse]`, `VFSBRIDGEURL: http://agent:8080/api/vfs-bridge`, `MCP__SHAREDSECRET`, and no `env_file`. No `security_opt` was added.

A ready probe on the prod host, after building the branch's image there:

```bash
docker run --rm --device /dev/fuse --cap-add SYS_ADMIN -e PUID=1654 -e PGID=1654 \
  --entrypoint bash mcp-sandbox:latest -c \
  'unshare -m sh -c "mount --make-rprivate / && mount -t tmpfs none /run && echo namespace-ok"'
```

Then the full path: start the stack, and from WebChat ask jonas to run `ls /vault | head -3` in the sandbox, then `cd /timers && ls -l` (the action files show as `---x--x--x`). If AppArmor refuses, try the narrowest option first — a profile allowing `mount fstype=fuse.*`, `fstype=tmpfs` and `options=(rprivate)` — and only then `security_opt: [apparmor:unconfined]` on `mcp-sandbox`. Record what was needed here. The spike branch's probe (`spike/fuse-sandbox`, `McpServerSandbox/prototype-fuse-spike/`) also still applies, including the mode-0111 ELF check.

**2026-10-01, done — a profile of its own was needed, unconfined was not.** Prod (`192.168.5.45`, Ubuntu 26.04.1, kernel 7.0.0-31, AppArmor parser 5.0.2) enforces `docker-default`. The branch's image was copied there as `mcp-sandbox:vfs-fuse-probe` (prod's `:latest` untouched) and driven through the real launcher: a Python stand-in for the server served a fake bridge on localhost and asked the launcher socket for one exec carrying a grant, so the real unit, daemon and helper ran.

- `docker-default`: `cannot isolate the command: Permission denied` — every exec fails, as predicted above.
- `apparmor=unconfined` and `apparmor=ziggurat-sandbox`: exit 0; `/vfs/probe` listed with the action file as `---x--x--x`, its text read through `/probe/hello.txt`, the mode-0111 `vfs-action` ELF executed and returned the bridge's output (`go-rc=0`), mountinfo showed `/run/vfs tmpfs` and `/vfs ziggurat-vfs`, `/proc/self/attr/current` read `ziggurat-sandbox (enforce)`, and the kernel log held no denial.
- Under the profile, a tmpfs on `/mnt` and a bind mount of `/etc` are still refused.

The profile is `DockerCompose/apparmor/ziggurat-sandbox`: `docker-default`'s rules with `deny mount` replaced by exactly the launcher's three — `options=(rprivate) -> /`, `fstype=tmpfs options=(rw, nosuid, nodev, noexec) tmpfs -> /run/vfs/`, `fstype=fuse options=(rw, nosuid, nodev) ziggurat-vfs -> /vfs/`. Compose names it in `security_opt`, and so does `SandboxContainer.AsCompose()`; a host without AppArmor (the dev box) ignores the option. It is **installed on prod** at `/etc/apparmor.d/ziggurat-sandbox` and loaded, so it survives a reboot; on a new host, run the install lines in its header before `up`, or the sandbox container refuses to start.

**2026-10-01, later — user namespaces closed, on two layers.** Asked how secure the result was, I found that under the profile above a command (PUID, no capabilities) could still run `unshare -Ur` and be root in a user namespace of its own: Docker relaxes its seccomp filter for a container holding SYS_ADMIN, for every process in it, and Ubuntu's `apparmor_restrict_unprivileged_userns=1` covers only unconfined processes. On master's container (no SYS_ADMIN) the same command is refused. Fixed twice, so neither layer is the only one:

- The profile declares `abi <abi/4.0>,` and `deny userns,`. On prod, a plain user under it alone gets `Permission denied`.
- The launcher installs a seccomp filter on every command after no-new-privs (`sandbox-runtime/src/seccomp.rs`): `unshare`/`clone` with `CLONE_NEWUSER` are EPERM, `clone3` is ENOSYS (glibc falls back to `clone`, as on a default Docker container), another ABI is ENOSYS. On prod unconfined it alone gets `Operation not permitted`, and it is what holds on the dev box, which has no AppArmor. Pinned by `SandboxLauncherE2ETests.ACommand_CannotUnshareOrMount` and `ACommand_StillMakesThreadsAndProcesses`, and a forked-child test in the crate.

The updated profile is reinstalled on prod. The full launcher path passes again under it, with threads working and no denials.
