# 14 — Check FUSE under prod's AppArmor

**What to build:** A person runs the sandbox image on the prod host with the FUSE device and capability, mounts through the launcher as 07 does, and runs a mode-`0111` ELF from the mount. If AppArmor refuses the mount, they add the narrowest security option that works (a profile allowing the FUSE mount before unconfined) to the deployment. The spike branch's probe script is a ready test. Prod access: ask the person if it isn't already set up.

**Blocked by:** 07 — The shell reads every mount

**Status:** ready-for-human

- [ ] The mount succeeds on prod, or the narrowest working security option is added and recorded on this ticket
- [ ] A mode-`0111` ELF served through FUSE executes on the prod kernel

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
