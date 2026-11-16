---
# A household's agent that answers aloud, which its owner adds on the Agents page and names (the
# household's agents section gives it its name and custom instructions; nothing else here is the
# owner's to change). No household gets one by default.
#
# A household's satellites go to the agent its owner marks to answer aloud, and with none marked to
# its first agent of this template (HouseholdAgents.VoiceAgentOf), so adding one is setting voice up.
# Any agent serving a satellite reads the voice section; this one also routes for a fast first word,
# and reads that section in the chat client too. Voice is one more channel
# of the household's, so a command spoken to it reaches everything the agent can do: every one of the
# tenant kit's own servers by server name — Home Assistant and the library among them — each reached
# only where the household set it up (a live tunnel credential for the name), beside the shared
# servers. It is the home's Nabu without the home's tuning: the voice section and the spoken-reply
# routing — a fast, cheap first word over a thorough one — and no Music Assistant, which the kit does
# not run. The language is the household's, as every template's is; the home's Nabu pins Spanish
# because the home speaks it.
name: nabu
purpose: A household's voice assistant, named by its owner, whose every reply is spoken aloud.
agent-name: Nabu
model: deepseek/deepseek-v4.1-flash-1017
reasoning-effort: low
# The home's Nabu's routing: the fastest provider first, with a latency it prefers, and never morph,
# which drops a tool's ref after the first call (2026-10).
provider-routing: {"sort": "throughput", "preferredMaxLatency": {"p50": 1}, "ignore": ["morph"]}
servers: vault, sandbox, websearch, homeassistant, library, http://mcp-scheduling:8080/mcp, printer, http://mcp-timers:8080/mcp
features: filesystem, subagents, memory
whitelist: domain__filesystem*, mcp__websearch*, domain__subagents*, domain__memory*
prompt-sections: voice
uses-outposts: true
---

Voice assistant driven by speech, with the tools its household gives it; every reply is spoken aloud, so answers are one short sentence of plain words — no formatting, no narration, no explanation.
