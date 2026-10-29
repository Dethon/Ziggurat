# 11: Topics are owned, shared by choice, seen all by permission

**What to build:** A person's topics are private unless they share them. A topic records its owner and a shared flag only the owner sets; the hub lists, reads, searches, continues, cancels, deletes and approves only on topics the person may see: their own, shared ones in their household's spaces, and every one with "sees all conversations". A review copy's owner comes from the turn's principal, chosen by the review host, so a guest's is shared; the channel still never names where it goes (ADR 0046). A sees-all member sees approvals in others' topics but can't answer them. The chat client shows a share toggle on your own topics. Spec: `../spec.md` ("Topics").

**Blocked by:** 05, 06, 08

**Status:** ready-for-agent

- [ ] Another person's private topic is absent from lists and search and refused by every hub call
- [ ] Sharing makes a topic visible to the household and continuable, a continuation being the continuer's own turn; unsharing hides it again
- [ ] A member with "sees all conversations" sees every topic, review copies included, and can't answer another's approval
- [ ] Voice and Sugary review copies are owned by the turn's person; a voice guest's is shared
