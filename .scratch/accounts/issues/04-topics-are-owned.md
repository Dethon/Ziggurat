# 04: Topics are owned, shared by choice, seen all by permission

**What to build:** A topic records its owner and a `Shared` flag only the owner sets. The hub lists, reads, searches, continues, cancels, deletes and approves only on topics the connection's user may see: their own, shared ones in their household's spaces, and every one of the household with `SeesAllConversations`. A review copy belongs to the turn's person; a voice guest's and a Telegram group's are shared. The client shows a share toggle on your own topics and who holds "sees all". Spec: `../spec.md` ("Topics").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] Another person's private topic is absent from lists and search and refused by every hub method (hub tests)
- [ ] Sharing makes it visible and continuable by household members; their message is their own turn
- [ ] A member with "sees all" sees every topic, review copies included
- [ ] Voice and Sugary review copies are owned by the turn's person; a guest's is shared
