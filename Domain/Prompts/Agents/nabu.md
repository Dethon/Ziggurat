---
# A household's agent that answers aloud, which its owner adds on the Agents page and names (the
# household's agents section gives it its name and custom instructions; nothing else here is the
# owner's to change). No household gets one by default.
#
# Its id is the one the voice server addresses (VoiceSettings.AgentId, agentDefaults.byChannel.voice),
# so a household's own voice server reaches it with no setting of its own. It is the home's Nabu
# without the home: the voice section and the spoken-reply tuning — a fast, cheap first word over a
# thorough one — and none of the home's Home Assistant tuning or Music Assistant, which a household's
# kit does not stand in for. The language is the household's, as every template's is; the home's
# Nabu pins Spanish because the home speaks it.
#
# The tenant kit's own servers are named by server name, as the Jonas template's are, and each turn
# reaches only those the household set up (a live tunnel credential for the name); the kit's Home
# Assistant is not named.
name: nabu
purpose: A household's voice assistant, named by its owner, whose every reply is spoken aloud.
agent-name: Nabu
model: deepseek/deepseek-v4.1-flash-1017
reasoning-effort: low
# The home's Nabu's routing: the fastest provider first, with a latency it prefers, and never morph,
# which drops a tool's ref after the first call (2026-10).
provider-routing: {"sort": "throughput", "preferredMaxLatency": {"p50": 1}, "ignore": ["morph"]}
servers: vault, sandbox, websearch, http://mcp-scheduling:8080/mcp, printer, http://mcp-timers:8080/mcp
features: filesystem, subagents, memory
whitelist: domain__filesystem*, mcp__websearch*, domain__subagents*, domain__memory*
prompt-sections: voice
uses-outposts: true
---

Voice assistant with the personal-assistant toolset (notes, web search, printing, timers, scheduled tasks), driven by speech; every reply is spoken aloud, so answers are one short sentence of plain words — no formatting, no narration, no explanation.
