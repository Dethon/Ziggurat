namespace Domain.DTOs.Channel;

public enum MessageOriginKind
{
    Schedule,
    Download,

    // A home watch fired: Home Assistant carried the prompt back through the Home Assistant
    // server's callback. Nothing that counts schedules counts these.
    Watch
}