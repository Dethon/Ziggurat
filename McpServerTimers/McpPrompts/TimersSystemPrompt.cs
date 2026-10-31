using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerTimers.McpPrompts;

[McpServerPromptType]
public class TimersSystemPrompt
{
    // Without a roster. Where a timer can ring is its household's — its own voice server's
    // satellites, or none — and a prompt is fetched for nobody while a session is built and cached
    // once for every household this server serves, so a roster here would show every household the
    // one it was fetched from. The household's satellites are read per call from
    // /timers/satellites.json instead, through the call's own household.
    [McpServerPrompt(Name = TimerPrompt.Name)]
    [Description(TimerPrompt.Description)]
    public string GetTimerPrompt() => TimerPrompt.Build([]);
}