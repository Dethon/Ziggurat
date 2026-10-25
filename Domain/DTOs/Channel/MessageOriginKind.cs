namespace Domain.DTOs.Channel;

public enum MessageOriginKind
{
    Schedule,
    Download,

    // A home watch fired: Home Assistant carried the prompt back through the Home Assistant
    // server's callback. Nothing that counts schedules counts these.
    Watch,

    // A person finished a login handoff and pressed Done: the browse server wakes the agent in the
    // conversation that asked for it, so the task carries on logged in.
    Login
}