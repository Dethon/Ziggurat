# Owners see and take back the links the household handed out

Status: needs-triage

Found in manual test 14 (2026-11-05). An invite, a Telegram group code and a recovery link are shown
once, to the owner who minted them, and never again: the members page lists people, not links. An owner
cannot see which invites are still open, for whom, minted by whom and until when, and cannot take one
back — an invite sent to the wrong chat, a group code pasted in the wrong group, stays open for its week.
Since a380c0f8a a link closes when whoever minted it stops managing the household (demoted or disabled),
and since 621b7d517 a person's newest recovery link closes their earlier ones, but an owner in good
standing has no way to close a link they regret.

## What is wanted

- `GET /accounts/admin/household/members/links`: every open link of the household (kind, the name an
  invite will create or the person a recovery link is for, its role, who minted it, minted and expiry
  times) — never the token, which is only ever hashed.
- `DELETE /accounts/admin/household/members/links/{linkId}`: closes it (spent, as if used), recorded
  ("Closed an invite for <name>") without the token.
- The members page lists them under "Open links", each with Close behind a confirmation.

## Open questions

- Should a closed link's page say it was taken back, rather than the generic "can no longer be used"?
- Should the operator's own owner links (created on the dashboard) be listed to the household's owners?
