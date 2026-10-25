# 04: One pinned fingerprint across Camoufox restarts

**What to build:** The Camoufox service presents the same device every launch. A fingerprint is generated once, kept in a volume of the Camoufox service, and passed to every launch; a missing file generates and saves one. Anonymous and person contexts share it. A Camoufox package bump that stops honouring the pin fails the image build loudly, like the existing anchor-checked patches. Spec: `../spec.md` ("Fingerprint").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] Two launches over the same volume report identical user agent, platform, screen size, core count and fonts
- [x] A fresh volume generates a fingerprint on first launch and reuses it afterwards
- [x] The image build fails if the pinned package no longer accepts the saved fingerprint
- [x] An integration test compares two launches' fingerprints
- [x] Compose declares the volume; no new environment entry unless a per-deployment value is needed
