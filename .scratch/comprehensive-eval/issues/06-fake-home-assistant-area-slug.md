# 06 — An area whose slug and display name disagree

Status: ready-for-agent

`ha.area-slug-is-read-not-derived` needs an area whose slug cannot be derived from its display
name, and an action that takes an area id. Add both to `FakeHomeAssistant`; the scenario asks
for the room by its display name and asserts the action ran with the slug the files say, not the
one the name suggests.
