# 02: Lemonade forwarder kit service

**What to build:** A household's tenant kit can run a **Lemonade forwarder**. It is a kit service of its own, a small .NET host on the hosting library's tunnel client with no MCP tools. It opens a tunnel to the deployment under the server name `lemonade` and takes streams on one listener tagged `chat`. It passes every request it gets to the household's box at the API base set in its environment (the same form an address box takes, ending in the API path), and streams the answer back unbuffered, because Lemonade answers as server-sent events. When the kit's `.env` holds a box key, the forwarder adds it as the bearer header. It takes no other destination and makes no back-call. With no box address it refuses to start and says so.

**Blocked by:** 01 (its tests reach it through the relay transport).

**Status:** done

- [ ] `lemonade` joins the household server names the deployment itself finds, and `chat` joins the tunnel protocol's tags.
- [ ] Tunnelled to the relay host with a stub Lemonade behind it (a small Kestrel answering the model list and a streamed Responses call), a request through ticket 01's transport for the household's `lemonade` and `chat`:
  - reaches the stub;
  - gets the stub's models;
  - streams a Responses answer chunk by chunk.
- [ ] With a key in the forwarder's environment the stub sees that bearer, and a key sent by the caller is replaced. With none set, no Authorization header reaches the stub.
- [ ] A request with an absolute URI or another Host still goes only to the configured box.
- [ ] The forwarder refuses to start with no box address, naming the missing setting.
- [ ] The tenant kit's compose file has the forwarder service:
  - tunnelled by the kit's usual hub, credential (`TUNNEL_CREDENTIAL_LEMONADE`) and server-name variables;
  - with the box address and an optional key;
  - with no published port.
- [ ] The `.env` template has the forwarder's lines, and the README's server table has its row: name `lemonade`, no back-calls, what it reaches.
- [ ] The tenant kit conformance tests cover the new service like every kit server.
- [ ] The tunnel rules record the new server name and tag, and that the server makes no back-call and publishes no metrics.
