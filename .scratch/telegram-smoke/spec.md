# Telegram in the smoke tier

Status: needs-triage

## Problem

The smoke stack starts no Telegram channel (`scripts/smoke-stack.sh`: "no Telegram bot (it would take the
deployment's updates)"), so no smoke test asks a running stack what the Telegram channel does for
households: linking by `/start`, `/agent`, `/web`, `/bind`, a household's own bot refusing another
household, approvals and alerts offered there, fires delivered there. Pass 20 (2026-11-06) drove all of it by
hand and found five defects the unit tests could not see together (a private chat's plain text dropped, a
switched agent's missing review copy, a timed-out approval left live, a group's "as you" wording, a fire to
Telegram delivered nowhere).

## How pass 20 drove it

A stand-in for the Bot API (`~/.cache/ziggurat-manual-driver/p20/tg/fake.py`, Python stdlib):

- on the stack's network under the alias `api.telegram.org`, TLS with a scratch CA that is appended to the
  channel container's `/etc/ssl/certs/ca-certificates.crt` (bind-mounted), so the shipped client code is
  unchanged and nothing reaches the real Telegram;
- the accounts service's key probe pointed at its plain-HTTP port (`KEYPROBES__TELEGRAMURL`), and
  `TELEGRAM__BOTUSERNAME` set, so the account and members pages show `t.me/<bot>?start=` links;
- a control port to inject updates (`/say` text, photo, voice, document, video, groups, forum threads;
  `/tap` callback queries) and to read every call the bots made (`/sent`, `/calls`);
- `sendMessage` refused (403) to a chat that never wrote to that bot, legacy-Markdown and HTML parse errors
  and over-long messages refused (400) as Telegram does, file ids per bot.

## Wanted

A `scripts/smoke-stack.sh telegram` (or part of `up`) that starts the stand-in and the channel this way, and
smoke tests that onboard two households over it: an invite redeemed by `/start`, a stranger answered with the
ways in, `/agent` listing each person's own household's agents, a household bot refusing the other
household, a group bound by `/bind` admitting only its household, a reminder set "por Telegram" delivered to
the person's private chat, and every message the bots sent landing only in the sender's own chat or bound
group.

## Not in scope

The real Telegram API: the stand-in's parse checks approximate legacy Markdown, so a formatting finding still
needs a real chat to confirm.
