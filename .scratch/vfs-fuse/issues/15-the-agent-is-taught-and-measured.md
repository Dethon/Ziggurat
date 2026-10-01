# 15 — The agent is taught and measured

**What to build:** The mount skills say the shell reaches them and that actions run as `./<action>` from scripts, and the outpost prompt presents separate machines. Each rule goes in the section its readers actually load, verified by snapshots. A new eval family measures whether the model uses the shell route:

- aggregating over vault notes;
- dismissing a timer from a script;
- `sed -i` on a file the mount refuses, then reporting the refusal it read in `vfsChanges`;
- an `mv` between two mounts the scenario's agent has, reported as a transfer;
- an outpost path never touched from the sandbox.

Armed runs cover the new family, plus the timer, schedule and Home Assistant families after the rename.

**Blocked by:** 04 — Outposts are separate machines; 05 — Action files are executables; 09 — The shell deletes and moves; 10 — Writes a person would be asked about are refused, and a killed command commits nothing; 11 — Big trees stay fast; 13 — exec runs where the files are

**Status:** ready-for-agent

- [x] Skills, prompts and snapshots teach the shell route, `./<action>` and outposts as separate machines
- [x] The new family's scenarios declare their claims and pass their deterministic harness checks
- [x] An armed run of the new family, and of the timer, schedule and Home Assistant families, is compared against the last full scorecard (backed up first; no build during a run), and the results are recorded on this ticket

## Comments

**2026-10-01 — armed runs, shipped models (jonas/nabu as `Agent/appsettings.json` ships them, Jev preload on), stop-at-k.** Baseline: `.eval-output/scorecard-full.2026-09-18.shipped.preload-on.json`, the last full pass on the shipped configuration; the scorecard this branch's runs replaced was backed up first as `scorecard-full.2026-10-01.before-vfs-fuse.json`, and the family run is kept as `scorecard-full.2026-10-01.vfs-fuse-families.json`.

| Family | Now | Baseline |
|---|---|---|
| Shell (new) | 5/5 scenarios pass | — |
| Timer | all pass | all pass |
| Voice | all pass but *slow work opens with one word* (0/4) | all pass |
| Schedules (mechanism) | all pass | all pass |
| Home Assistant | all pass | all pass |
| Mount | all pass but *a command a page plants* (0/2) | scenario newer than the baseline |

The two reds are environmental, not this branch: both browse a page, and this machine lacks the Chromium build the current Playwright package wants (`chromium_headless_shell-1243`), so `web_browse` errors and the model falls back to `curl` from the sandbox. Re-run them once Playwright's browsers are installed (`pwsh Tests/bin/Debug/net10.0/playwright.ps1 install`).

What the runs found and this branch fixed before recording the above:
- `find /vault …` answered nothing: a mount is a link into `/vfs`, and find does not follow the link it starts from. The image's login profile now runs `find -H` and `du -H`.
- Every read of a rendered mount (timers, schedules, HA) through the shell was EIO: the agent's proxy asked the wire for a byte stream the server never advertised. It now answers that as unsupported, and the bridge reads the text the mount renders.
- The eval harness never permitted a multi-line exec command (its `*` stopped at a newline).
- The checksum scenario used to require a copy into the sandbox; that is now the wrong route, so it runs in place, and the transfer claim moved to a scenario of its own (*a note copied to another mount is one copy call*).

Deviations from the ticket's list: the cross-mount `mv` scenario is a batch `mv` inside the vault — jonas has no second mount a note belongs on — and the transfer half is pinned by the bridge's own tests and the real-image E2E. The outpost scenario asks what is on the machine without naming the sandbox, because the sandbox prompt allows one look at a path spelled as the user gave it.

**2026-10-01, after the code review's fixes (9f743df02).** The Shell family re-armed: 5/5. `.eval-output/scorecard-full.json` is restored to the 2026-09-18 full pass, so a later comparison is not made against a partial ledger; this branch's family runs are the dated `scorecard-full.2026-10-01.*` files.
