---
name: user_context
purpose: The user every user-scoped tool call in this conversation belongs to.
priority: UserContext
budget: 100
# The id of the user who created the conversation.
placeholders: user
---

## User Context
Conversation created by user: '{{user}}'
Use this userId/username for all user-scoped operations. unless you get more updated information in the user's message
