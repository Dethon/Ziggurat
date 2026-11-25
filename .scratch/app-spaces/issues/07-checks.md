# 07: Checks

Spec: `.scratch/app-spaces/spec.md` ("Checks").

**What to build:** The agent sees what it built before the person does. `./check [--viewport WxH]... [--path <route>]` in an app's directory opens the preview in a headless Chromium inside the apps server's image. It signs in as the person whose turn ran the check, with an app session the server makes from the call's verified principal. Screenshots and a report land in `/apps/<slug>/.checks/latest/`, where `file_read` shows the PNGs as images. The browser reaches app origins at the apps server itself. Data reads go through `/_vfs/*`, so they return data once 06 lands.

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] One PNG per requested viewport. The default viewports are a phone and a desktop.
- [ ] `report.json` records console messages, failed requests, content-policy violations, an accessibility snapshot, and each assertion file under `checks/` with its pass or fail and message.
- [ ] Assertion files run inside the page only. Nothing of the app runs outside the browser.
- [ ] Concurrent checks beyond the per-household or total quota are refused, not queued. A check past its deadline is stopped and its report says so.
- [ ] Tests run real Chromium in process: a screenshot is present, a console error is reported, a CSP violation is reported, and one assertion passes while another fails.
