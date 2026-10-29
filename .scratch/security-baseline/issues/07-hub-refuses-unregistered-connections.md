# 07: The hub refuses unregistered connections; approval ids are unguessable

**What to build:** Every `ChatHub` method except `RegisterUser` refuses a connection that hasn't registered. Approval ids become a full GUID (32 hex characters) on SignalR and Telegram. Per-user checks wait for Accounts, where the user is real. Spec: `../spec.md` ("Hub registration").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] An unregistered connection calling `GetHistory`, `GetTopicPage`, `SearchTopics`, `SaveTopic`, `DeleteTopic`, `CancelTopic`, `StartSession` or `RespondToApprovalAsync` is refused (hub tests)
- [ ] A registered connection keeps working (existing hub and E2E tests)
- [ ] Approval ids are 32 hex characters on both channels, and Telegram callback data stays within 64 bytes
