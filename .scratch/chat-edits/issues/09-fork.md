# 09: Fork from here

Status: done
Blocked by: 01, 03
Spec: ../spec.md (Fork, Who may do what)

## What
- **Hub.** `Fork(topicId, entryId)`, open to anyone who sees the topic. It works on mirrored topics too.
  - On a reply, it copies the active line through the end of that reply's turn.
  - On a user message, it copies up to just before it and answers that message's text, so the client can put it in the composer.
- **The new topic.** It is created as `SaveTopic(isNew)` creates one: the forker's, private, in the same space, on the same agent, named `<name> (fork)`. The copied messages get new entry ids, and the topic record is stamped (count, snippet, search). The topic opens read for the forker.
- **Attachments.** Files are copied into the fork's conversation directory and their references repointed. Find out what `SandboxPaths` refers to. Then either copy it or drop it, and say which in the code.
- **Client.** Fork in the menu. It opens the new topic and prefills the composer when the hub answered text.

## Acceptance
- Hub tests: a sees-all viewer can fork someone else's private topic, and the fork is theirs and private. A mirrored topic forks.
- The fork's attachments download after the source topic is deleted.
- E2E: fork from a reply, and the new topic shows the copied line.
