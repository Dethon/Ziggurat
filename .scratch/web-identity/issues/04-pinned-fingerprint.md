# 04: One pinned fingerprint across Camoufox restarts

**What to build:** The Camoufox service presents the same device every launch. A fingerprint is generated once, kept in a volume of the Camoufox service, and passed to every launch; a missing file generates and saves one. Anonymous and person contexts share it. A Camoufox package bump that stops honouring the pin fails the image build loudly, like the existing anchor-checked patches. Spec: `../spec.md` ("Fingerprint").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Two launches over the same volume report identical user agent, platform, screen size, core count and fonts
- [ ] A fresh volume generates a fingerprint on first launch and reuses it afterwards
- [ ] The image build fails if the pinned package no longer accepts the saved fingerprint
- [ ] An integration test compares two launches' fingerprints
- [ ] Compose declares the volume; no new environment entry unless a per-deployment value is needed
