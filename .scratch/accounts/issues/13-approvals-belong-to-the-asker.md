# 13: Approvals belong to the asker

**What to build:** Nobody approves for anyone else, and a person can answer wherever they are. An approval records the asking principal and only that user's answer settles it; it is shown where the turn asked and also offered in the person's Telegram private chat and as a chat-client notification; the first answer wins and the others show answered elsewhere; a fire's approval goes to the schedule's creator. Spec: `../spec.md` ("Approvals").

**Blocked by:** 07, 11

**Status:** done

- [x] Another person's answer is ignored on every channel (hub: `ChatHubTopicVisibilityTests.AnApprovalIsItsAskers_EveryoneElsesAnswerIsIgnored`, `AnApprovalOfferedToItsAsker_…`; Telegram: `TelegramApprovalOfferTests`; voice: `RequestApprovalToolTests.RequestMode_AnotherPersonsYes_IsIgnoredForAnAccountsQuestion`)
- [x] The asker's Telegram answer settles a voice-asked approval, and the chat-client card shows answered elsewhere (proven seam by seam: `ApprovalRelayTests.TheAskersTelegramAnswer_SettlesAVoiceAskedApproval_…` at the agent, the hub's settle telling every card `AnsweredElsewhere`, the client's `ApprovalCard` showing it; no E2E — a voice prompt and a Telegram tap cannot be driven in the WebChat E2E stack)
- [x] A fire's approval is offered to the schedule's creator only (`ApprovalRelayTests.AFiresApproval_IsOfferedToTheSchedulesCreatorOnly`, `TelegramApprovalOfferTests.AFiresApproval_IsTheSchedulesCreatorsAlone`, the hub's offer reaching that account's connections alone)

Notes for later tickets (`.claude/rules/channels.md` "An approval is its asker's" has the detail):

- Decided beyond the ticket: the channel the turn's delivery anchors stays authoritative about ending a question — its answer, refusal or time-out settles every offer, as before. Only an anchor that could not show it to anyone answers `no_answer` (SignalR with no live session, no satellite, arbitration), which leaves the offers two minutes (`ApprovalRelay.UnshownWindow`). So nabu still gives up after its two attempts, and a tap on Telegram counts while it is asking.
- Voice checks the answering voice only where it can: a conclusive other person, or a voice the verifier rejects, is asked again; a short "sí" (`Skipped`) is the asker's.
- `IChannelConnection` no longer is an `IToolApprovalHandler`: it has `AskApprovalAsync`/`OfferApprovalAsync` (null is no answer) and `SettleApprovalAsync`; the agent's handler is always `ApprovalRelay`, and `IToolApprovalHandler.RequestApprovalAsync` takes an `ApprovalAsk` carrying the turn as a field.
- Known edge: an anchor that answers by itself at once (a dual-role server's auto-approval, when a fire resolved no target) still has the offers sent and settled a moment later — a Telegram message edited to "Answered elsewhere", or a push to someone with no client open.
- `ApprovalCallbackRouter.RecordSender` stays, used only for a Telegram turn from before accounts; it is called from `TelegramBotService`, which this ticket did not touch (ticket 12 works there).
- The card and Telegram say "Answered elsewhere" for any settle, an anchor's time-out included (nobody answered). `settle_approval` already carries the result, so "Approved/Rejected elsewhere" or "No longer waiting" is a small follow-up.
- No E2E for "answered elsewhere" (spec seam 2): whoever writes one should know `WebChatE2ETests.RejectEveryVisibleApprovalAsync` waits for `.btn-reject`, and a card showing Close instead (not yours, answered elsewhere) loops it to its deadline.
- The Telegram keyboard's "Always" (`approved_and_remember`) used to reach the agent as a refusal (it never parsed as the enum); `ChannelProtocol.ParseApprovalAnswer` now reads it as approve-and-remember.
