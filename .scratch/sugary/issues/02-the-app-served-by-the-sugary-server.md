# 02 — The app served by the Sugary server

**What to build:** The Sugary PWA moves into the repository unchanged and is served by the Sugary server, with its HTTP API on routes other than `/mcp`, gated by the access code the standalone app uses (a deployment secret). She can open the app, add foods by hand, from her foods or by barcode, save a meal, edit portions, record the outcome, manage her foods and download the CSV tables. Estimating from a photo or description is not wired yet and says so. See the spec's "The Sugary server".

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The app loads from the Sugary server and asks for the access code; every API route refuses a request without it.
- [ ] Meals, foods, barcode lookup (her saved foods first, then Open Food Facts) and the CSV downloads behave as in the standalone app; the standalone API and export tests carry over as behaviour tests at this boundary.
- [ ] A meal saved in the app appears in `/sugary` on the next read.
- [ ] The estimate actions are visibly unavailable rather than failing.
- [ ] The PWA builds as part of the server's image; it shares no code with WebChat or the dashboard.
