# 14 — Check FUSE under prod's AppArmor

**What to build:** A person runs the sandbox image on the prod host with the FUSE device and capability, mounts through the launcher as 07 does, and runs a mode-`0111` ELF from the mount. If AppArmor refuses the mount, they add the narrowest security option that works (a profile allowing the FUSE mount before unconfined) to the deployment. The spike branch's probe script is a ready test. Prod access: ask the person if it isn't already set up.

**Blocked by:** 07 — The shell reads every mount

**Status:** ready-for-human

- [ ] The mount succeeds on prod, or the narrowest working security option is added and recorded on this ticket
- [ ] A mode-`0111` ELF served through FUSE executes on the prod kernel
