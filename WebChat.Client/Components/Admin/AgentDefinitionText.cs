using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.DTOs;

namespace WebChat.Client.Components.Admin;

// An agent's whole definition as the agents page shows it, read only: indented JSON in the casing its
// seeded form is written in, with what the definition leaves unset left out. A bot's token never
// rides on a household's definition (the section refuses one), and is left out here all the same.
public static class AgentDefinitionText
{
    private static readonly JsonSerializerOptions _shown = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Of(AgentDefinition definition) =>
        JsonSerializer.Serialize(definition with { TelegramBotToken = null }, _shown);
}