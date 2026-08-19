# 06 — An area whose slug and display name disagree

Status: resolved

`ha.area-slug-is-read-not-derived` needs an area whose slug cannot be derived from its display
name, and an action that takes an area id. Add both to `FakeHomeAssistant`; the scenario asks
for the room by its display name and asserts the action ran with the slug the files say, not the
one the name suggests.

## Answer

Done. The study: created as "Despacho", renamed to "Estudio", slug frozen at `despacho` the way
HA freezes them. A vacuum lives in it, and the new `vacuum.clean_zone` service takes
`cleaning_area_id` through HA's area selector, which the help renderer already flags as
"AREA_ID (slug)". The fake answers only to the frozen slug — the lowercased display name cleans
nothing — pinned in FakeHomeAssistantTests. Scenario "an area argument is the slug the files
say" cites `home.area-slug-is-read-not-derived`: "pasa la aspiradora por el estudio" must land
as `clean_zone.sh --cleaning_area_id despacho` with the vacuum ending at `cleaning`.
