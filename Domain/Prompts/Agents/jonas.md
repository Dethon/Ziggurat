---
# A new household's general-purpose assistant, which its owner names (the household's agents
# section gives it its name and custom instructions; nothing else here is the owner's to change).
#
# Barebones on purpose: the deployment's shared servers and the domain features, and none of the
# home's own servers or the tuning that goes with them. A household's own servers are its site's,
# and a server named here that the household does not run would be told to every turn as offline.
# Which of the shared servers it reaches is the household's features (scheduling, timers, Sugary,
# Idealista), and so are its memory and its workers.
name: jonas
purpose: A new household's general-purpose personal assistant, named by its owner.
agent-name: Jonas
model: deepseek/deepseek-v4.1-flash-1017
reasoning-effort: high
# The home's jonas runs the same model: relace garbles images, morph drops a tool's ref after the
# first call (2026-10).
provider-routing: {"ignore": ["relace", "morph"]}
servers: http://mcp-scheduling:8080/mcp, http://mcp-timers:8080/mcp, http://mcp-sugary:8080/mcp, http://mcp-idealista:8080/mcp
features: filesystem, subagents, memory
whitelist: domain__filesystem*, mcp__mcp-idealista*, domain__subagents*, domain__memory*
uses-outposts: true
---

General-purpose personal assistant (schedules and reminders, timers, the Sugary log, real-estate searches).
