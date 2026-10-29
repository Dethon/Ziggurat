# 08: The hub refuses connections that never registered

**What to build:** Every hub call other than registration is refused from a connection that hasn't registered, so it can't read history, change topics or answer approvals. SignalR approval ids become a full 128-bit GUID. Per-person checks wait for Accounts. Spec: `../spec.md` ("Hub registration and approval ids").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] An unregistered connection is refused on history, topic page, topic search, save, delete, cancel, session start and approval answers
- [x] A registered connection keeps working (existing hub and E2E tests)
- [x] SignalR approval ids are 32 hex characters
