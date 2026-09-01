# 06 — A Lemonade turn fits its model and is counted as Lemonade's

**What to build:** A long conversation switched to a Lemonade model still gets an answer, because the turn is truncated to the smaller of the agent's context window and the model's window discovered from the box; a model whose window is unknown uses the agent's. On the metrics dashboard, a Lemonade turn's token, latency and truncation events carry the namespaced id (`lemonade/<id>`) as the model and zero cost, so the model dimension never mixes a local model with a hosted one and the cost chart stays truthful. The served-model value the box reports (a file path) is never used.

**Blocked by:** 02 — The truncation window is decided per turn; 05 — A Lemonade turn is answered by the box.

**Status:** done

- [x] A Lemonade turn whose history exceeds the model's discovered window is truncated to that window before it is sent, and the truncation event reports that window
- [x] A Lemonade model with no discovered window truncates to the agent's window
- [x] An OpenRouter turn truncates to the agent's window exactly as before
- [x] Token, latency and truncation events for a Lemonade turn carry `lemonade/<id>` as the model and a cost of zero; the dashboard's model dimension shows them under that name
- [x] Tests: the routing client over a stubbed transport with the recording metrics publisher, following the existing truncation and chat client tests
