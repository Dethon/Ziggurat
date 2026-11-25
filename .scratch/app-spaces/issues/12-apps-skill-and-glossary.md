# 12: The `apps` skill and glossary terms

Spec: `.scratch/app-spaces/spec.md` ("The skill", "New domain terms").

**What to build:** The agent builds apps the same way every time. The apps server ships an `apps` skill, the way every skill is shipped by the server whose tools it teaches. It covers the app manifest, the client library, static versus npm builds (and that `node_modules` never enters `/apps`), CDN pinning with integrity, checks and assertion files, publish, history and rollback, and reading `errors`. `CONTEXT.md` gains the new terms: built app, app space, app manifest, preview, check, app sign-in.

**Blocked by:** 06, 07, 08

**Status:** ready-for-agent

- [ ] The skill is listed only to agents offered `/apps`, and stays within the skills budget.
- [ ] Every API, path and action file the skill names exists, checked by the skills' existing conformance tests.
- [ ] Each `CONTEXT.md` entry carries its _Avoid_ line. Built app avoids plain "app", which already means apps behind the proxy; app sign-in avoids "handoff".
