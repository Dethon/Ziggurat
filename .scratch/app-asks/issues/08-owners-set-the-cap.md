# 08: Owners set the cap

Spec: `.scratch/app-asks/spec.md` ("The cap").

**What to build:** A household owner sets a household default cap and a per-app override on their
apps admin page. The values live in the household configuration and are read by the apps server
through a view, as `spaces` is. The limit an ask meets is the app's override, else the household's
default, else the deployment's.

**Blocked by:** 07

**Status:** ready-for-agent

- [ ] A new part of the household configuration holds the default and the per-app overrides, saved by owners only, merging per item against what the page loaded (owner admin seam).
- [ ] The values are bounded: no negative cap. A per-app entry must name a published app of the household.
- [ ] The apps server reads the view on the viewer's assertion. A save is heard and re-read within the view's keep time.
- [ ] If the configuration cannot be read, asks are refused `transient_dependency`. The cap is never lifted (origin seam).
- [ ] The resolution order (override, household default, deployment default) is tested end to end (origin seam).
