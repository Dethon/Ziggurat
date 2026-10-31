# SexyTime: forward the assertion to the agent registration API

For SexyTime's own repository. Ziggurat's ticket 06 (Service identity) changed what the agent's
`/api/agents*` believes; SexyTime's server must change with it or every call it makes is refused.

## What changed in Ziggurat

`/api/agents` (GET list, POST register, DELETE `/{agentId}`) used to take the person from the
`X-Ziggurat-User` header beside the shared secret. It now ignores that header. It wants both of:

- `Authorization: Bearer <AGENTAPI__SHAREDSECRET>`, as before: says the caller is SexyTime's server.
- `X-Ziggurat-Assertion: <token>`: the accounts service's signed assertion of who the call is for.
  The agent checks it against the accounts service's published keys. It must be signed for the
  channel `sexytime`, must name a person (not the household guest), and must still be in date.

With the secret and no valid assertion the API answers **401**. Before, a missing user got a 400.
The person a custom agent is filed under is the assertion's subject (the account's user id). That
is the same value the old `X-Ziggurat-User` carried, so existing registrations keep their owner.

## What SexyTime's server must do

1. Every request the proxy hands SexyTime (`sexy.herfluffness.com`, behind
   `forward_auth ... /accounts/verify?channel=sexytime`) carries the header `X-Ziggurat-Assertion`,
   beside `X-Ziggurat-User`, `-Tenant` and `-Roles`. Read it off the incoming request that the
   `/api/agents` call is made on behalf of.
2. Copy it **verbatim** onto the outgoing call to the agent: header name `X-Ziggurat-Assertion`,
   one value. Keep sending `Authorization: Bearer <secret>`. Sending `X-Ziggurat-User` too is
   harmless, but nothing reads it.
3. Do not cache it, store it or reuse it across browser requests. It is valid for ten minutes and
   names one channel. Forward the one on the request being answered.
4. Do not mint, edit or re-sign it, and do not take it from a client-supplied field. The proxy's
   `copy_headers` sets it over anything the browser sent. Any other source is unsigned or foreign,
   and the agent refuses it.
5. Treat a 401 from `/api/agents` while holding the secret as "this person is signed out or their
   assertion is not believable". Typical causes: an expired assertion forwarded late, a missing
   header, or an assertion from another route of the deployment such as the chat hub's (`signalr`).
   Do not retry it as a secret problem.

## Example

```
GET http://agent:8080/api/agents
Authorization: Bearer <AGENTAPI__SHAREDSECRET>
X-Ziggurat-Assertion: eyJhbGciOiJFUzI1NiIsImtpZCI6...   (copied from the incoming request)
```

## Rollout order

Deploying Ziggurat's change first breaks SexyTime's agent management until SexyTime forwards the
header. Deploying SexyTime's change first is harmless, because the old agent ignores the extra
header. Ship SexyTime first.
