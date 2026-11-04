# 01: Relay transport to a household's tunnelled server

**What to build:** A prefactor that later tickets build on. The agent can make its own HTTP requests to a listener of one household's tunnelled server, chosen by household, server name and tag. Every connection is a new stream of that household's open tunnel, and nothing goes onto the network. Today the relay can only pass along an exchange that arrived from outside (the browse pages, through the tunnel forwarder). This adds the same tunnel streams as a transport that agent code can hand to an HTTP client. The tunnel is looked up again for each connection, by household and server name together, so a reopened tunnel is used at once.

**Blocked by:** None (can start immediately).

**Status:** done

- [ ] A request sent through the transport for household A, server S and tag T reaches the listener that A's tunnelled S registered under T, and its answer comes back streamed (the first chunk arrives before the server finishes).
- [ ] When household B has an open tunnel for a server also named S, a request for A never reaches B's server, and a request for a household with no S fails.
- [ ] When the tunnel is closed, a connection fails with an error that says the server is offline. After the tunnel reopens with a new generation, the next request goes through with no new transport.
- [ ] A tag the tunnelled server does not take is reset, and the request fails.
- [ ] Tested with the existing relay and tunnelled-server test hosts (the browse pages tests are prior art). The existing browse-page forwarding keeps passing unchanged.
