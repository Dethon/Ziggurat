namespace McpChannelTelegram.Services;

// A command as a person sends it: "/start@ziggurat_bot CODE" is how a group sends one picked from the
// bot's menu, so the bot's name is dropped and the name is read without case.
public static class TelegramCommand
{
    public static (string Name, string Argument)? Of(string content)
    {
        if (!content.StartsWith('/'))
        {
            return null;
        }

        var parts = content.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? null
            : (parts[0][1..].Split('@')[0].ToLowerInvariant(), parts.Length > 1 ? parts[1] : "");
    }
}