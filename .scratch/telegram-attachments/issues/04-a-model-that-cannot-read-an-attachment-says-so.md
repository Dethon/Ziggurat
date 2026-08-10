# 04 — A model that cannot read an attachment says so and stops the turn

**What to build:** Sending a photo to a bot whose agent runs on a text-only model produces a reply
naming that model and saying it cannot read images, and no turn runs at all.

This refusal is not like the two in 02. Those are properties of one file and drop only that file.
This one is a property of the turn: a model that cannot read what was attached would answer as
though nothing had been sent, and an answer that silently ignores the question is worse than no
answer. So the whole message stops, files and caption together.

For the channel to know this, it starts accepting the agent's catalogue registration — the same
call the agent already makes to every channel on connect and on every reconnect. The channel asks
the same capability resolution WebChat asks, from the same catalogue shape, so the two cannot
disagree about which model is refusing. It is permissive wherever the catalogue is silent: before
any agent has connected, or where a model is unknown, attachments go. A blip must not remove the
feature.

Nothing else consumes the catalogue on this channel and no new command is added. Telegram has no
per-message model override, so the model a turn will run on is always the agent's default.

**Blocked by:** 02 — A file that cannot be sent is refused in the chat.

**Status:** ready-for-agent

- [ ] The channel accepts the agent catalogue registration and replaces any previously registered set.
- [ ] Attaching an image to an agent whose model does not accept images produces a reply naming the model, and no turn is emitted.
- [ ] The same holds for documents against a model that does not accept them.
- [ ] With no catalogue registered, or an agent absent from it, attachments go through.
- [ ] A message whose attachments the model accepts is unaffected.
- [ ] Tests register a catalogue through the tool's own entry point and then drive media through the polling service, asserting both the reply and the absence of a notification.
