---
paths:
  - "McpServerScheduling/**"
  - "Domain/Tools/Scheduling/**"
  - "Domain/Prompts/SchedulingPrompt.cs"
  - "Domain/DTOs/Schedule.cs"
---

# Scheduling Architecture

`McpServerScheduling` is a dual-role MCP server:
- **`filesystem://schedules` resource** (mount `/schedules`) — managed with the standard `domain__filesystem__*` tools. Layout: `/schedules/<agentId>/<scheduleId>/schedule.json` (`{prompt, cron|runAt, userId?, deliverTo?}` — exactly one of recurring `cron` or one-shot `runAt`), plus `agent_info.json` and read-only `status.json` (`createdAt`/`lastRunAt`/`nextRunAt`). `fs_exec run_now.sh` on a schedule directory fires it immediately. The `ScheduleFileSystem` engine (`Domain/Tools/Scheduling/Vfs/`) implements `IFileSystemBackend`, returning typed `FsResult<T>`.
- **Channel** — registered through `AddChannelServer(DeliveryPolicy.GateOnLive)` (`Mcp.Hosting`). `ScheduleDispatcherService` polls `IScheduleStore` for due schedules, `ScheduleFirePlanner` chooses delete-after-fire (one-shot) vs. update-next-run (cron), and the shared `ChannelNotificationEmitter` emits `channel/message`. The notification itself — `deliverTo` parsed and coalesced into reply targets, the agent, the `userId`, the origin, one clock reading for the conversation id and the timestamp — is composed by `Domain/Channels/FirePlanning.cs`, which the Home Assistant server's watch callback uses too, so the two kinds of unprompted fire cannot drift. The default `deliverTo` is the shared policy file `Domain/delivery.json` (`DeliverySettings`), not a scheduling setting. Gate-on-live is load-bearing: the dispatcher deletes or advances a schedule only when the emit reports a live subscriber, and a false return buffers nothing, so a failed delivery leaves no duplicate to fire alongside the retry. The agent runs the prompt; `ChatMonitor` fans the result out to `deliverTo`, minting conversations as needed.

The `scheduling_prompt` (`Domain/Prompts/SchedulingPrompt.cs`) teaches the `/schedules` idiom.
