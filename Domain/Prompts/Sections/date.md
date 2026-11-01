---
name: date
purpose: Today's date. Last of the static sections, because it is the only one that changes on its own.
priority: Date
budget: 30
# The day, as "Friday, 2026-05-15", in the invariant culture, and the household's IANA zone in the
# 'zoned' part: the day is the household's, and a household away from the deployment's zone is told
# which zone it is, so "at eight" means eight where it lives. One in the deployment's own zone reads
# the main body, as every agent did before households had zones of their own.
placeholders: date, time_zone
---

Today is {{date}}.

<!-- part: zoned -->

Today is {{date}}, in the {{time_zone}} time zone.
