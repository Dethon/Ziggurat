# 03: Serving built apps on the apps domain

Spec: `.scratch/app-spaces/spec.md` ("Serving built apps").

**What to build:** A built app's author opens its published origin and its preview origin in a browser and gets the app. Each origin has its own content policy, web manifest and service worker, so it installs as a PWA of its own. The apps domain is a per-deployment compose value. Caddy has one `*.<apps-domain>` block with a wildcard certificate through the existing Cloudflare DNS-01 snippet, sending everything to the apps server with no `forward_auth`. In this ticket access is by an app session the apps server holds (seeded by tests). The sign-in that creates sessions is ticket 04.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The server parses `<slug>-<id>` and `<slug>-<id>-preview` from the Host header by the id. A renamed slug's old link still resolves, and an unknown label is a 404.
- [ ] The published origin serves the served commit's `root`. The preview origin serves the working tree to the author alone. Everyone else gets the same 404 as an unknown app.
- [ ] Every response carries the policy built from the manifest: `default-src 'none'`; `script-src`, `style-src` and `font-src` as `'self'` plus the configured CDN list; `img-src 'self' data: blob:` plus CDNs and `connect`; `connect-src 'self'` plus `connect`; `frame-ancestors 'none'`; `base-uri 'none'`; `form-action 'none'`.
- [ ] Every served HTML document has the manifest link, the service worker registration and the client library injected.
- [ ] A generated web manifest is served from the app's name and colour, with an icon drawn from the colour as the space icon is and `start_url`/`scope` set to `/`.
- [ ] A generated service worker is served that caches the app's own files and never caches `/_vfs/*`.
- [ ] A deleted app's origin answers navigations with a "this app was removed" page, and its service worker with one that unregisters itself.
- [ ] `./publish` refuses HTML that loads an outside script or stylesheet without an exact version and an `integrity` attribute, and an import map that names an outside module without an integrity entry.
- [ ] Caddy and deployment conformance tests cover the apps-domain block and its environment entry. The smoke and E2E stacks use a local wildcard name.
