---
# A new household's general-purpose assistant, which its owner names (the household's agents
# section gives it its name and custom instructions; nothing else here is the owner's to change).
#
# Barebones on purpose: the deployment's shared servers, the tenant kit's own servers by name, and
# the domain features, and none of the home's tuning. Which of the shared servers it reaches is the
# household's features (scheduling, timers, Sugary, Idealista), and so are its memory and its
# workers. Which of the own servers it reaches is what the household set up — a server name it holds
# a live tunnel credential for — asked at every turn: one it never set up is left out of the turn
# and never named, since a household told every turn that its vault is offline never had one. The
# kit's Home Assistant, voice and library are not named as servers this agent dials, though each is a
# channel the deployment follows for every household that runs it: its watches fire turns, its
# downloads announce themselves, and voice is answered by a voice agent (the nabu template). This
# template carries none of the home's Home Assistant or library tuning. Web search searches and reads
# pages for any household, and its login handoff opens on the household's own web search (a browse
# link names its household).
name: jonas
purpose: A new household's general-purpose personal assistant, named by its owner.
agent-name: Jonas
model: deepseek/deepseek-v4.1-flash-1017
reasoning-effort: high
# The home's jonas runs the same model: relace garbles images, morph drops a tool's ref after the
# first call (2026-10).
provider-routing: {"ignore": ["relace", "morph"]}
servers: http://mcp-scheduling:8080/mcp, http://mcp-timers:8080/mcp, http://mcp-sugary:8080/mcp, http://mcp-idealista:8080/mcp, vault, sandbox, websearch, printer
features: filesystem, subagents, memory
whitelist: domain__filesystem*, mcp__websearch*, mcp__mcp-idealista*, domain__subagents*, domain__memory*
uses-outposts: true
---

General-purpose personal assistant (schedules and reminders, timers, the Sugary log, real-estate searches).
