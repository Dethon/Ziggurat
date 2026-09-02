# 04 — An extraction the box cannot answer falls back

**What to build:** a WebChat user whose Lemonade chat host is away or refusing when the extraction runs still gets the turn remembered. After the worker's existing retries all end in the named Lemonade host error, the worker logs one warning naming the host's address and the model, publishes one error metric under the memory service, and extracts the same window once more with no model, which is the configured extraction model; the extraction event that follows is the fallback's, so a fallback never reads as a silent success. Only the named host error triggers the fallback: a parse failure, a store failure or any other exception keeps its existing path. The fallback attempt gets no retries beyond what the existing loop gives an OpenRouter extraction.

**Blocked by:** 03 — A turn's extraction follows its Lemonade model.

**Status:** ready-for-agent

- [ ] Through the worker with a mocked extractor: when every attempt for a Lemonade model throws the named host error, the extractor is asked once more with no model, and the extraction event counts that attempt's candidates.
- [ ] Before the fallback attempt a warning is logged naming the address and the model, and an error metric under the memory service is published; the capturing logger and recording metrics publisher pin both.
- [ ] A Lemonade extraction that throws any other exception on every attempt does not fall back and follows today's failure path.
- [ ] A request with no Lemonade model that throws the host error does not fall back (there is nothing to fall back from) and follows today's path.
- [ ] The retry count remains the single existing option; no new retry setting.
