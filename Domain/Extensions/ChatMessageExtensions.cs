using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public static class ChatMessageExtensions
{
    private const string SenderIdKey = "SenderId";
    private const string TimestampKey = "Timestamp";
    private const string MemoryContextKey = "MemoryContext";
    private const string LocationKey = "Location";
    private const string SatelliteIdKey = "SatelliteId";
    private const string DismissedAlertKey = "DismissedAlert";
    private const string ConversationContextKey = "ConversationContext";
    private const string ConfigPatchKey = "ConfigPatch";
    private const string AttachmentsKey = "Attachments";
    private const string AttachmentChannelIdKey = "AttachmentChannelId";
    private const string SandboxPathsKey = "SandboxPaths";
    private const string LandingFailuresKey = "LandingFailures";

    extension(ChatMessage message)
    {
        public string? GetSenderId()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(SenderIdKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetSenderId(string? senderId)
        {
            if (senderId is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[SenderIdKey] = senderId;
        }

        public string? GetLocation()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(LocationKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetLocation(string? location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[LocationKey] = location;
        }

        public string? GetSatelliteId()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(SatelliteIdKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetSatelliteId(string? satelliteId)
        {
            if (string.IsNullOrWhiteSpace(satelliteId))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[SatelliteIdKey] = satelliteId;
        }

        public string? GetDismissedAlert()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(DismissedAlertKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetDismissedAlert(string? dismissedAlert)
        {
            if (string.IsNullOrWhiteSpace(dismissedAlert))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[DismissedAlertKey] = dismissedAlert;
        }

        public DateTimeOffset? GetTimestamp()
        {
            return ParseTimestamp(message.AdditionalProperties?.GetValueOrDefault(TimestampKey));
        }

        public void SetTimestamp(DateTimeOffset? timestamp)
        {
            if (timestamp is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[TimestampKey] = timestamp.Value;
        }

        public MemoryContext? GetMemoryContext()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(MemoryContextKey);
            return value switch
            {
                MemoryContext context => context,
                JsonElement je => je.Deserialize<MemoryContext>(),
                _ => null
            };
        }

        public void SetMemoryContext(MemoryContext? context)
        {
            if (context is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[MemoryContextKey] = context;
        }

        public ConversationContext? GetConversationContext()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(ConversationContextKey);
            return value switch
            {
                ConversationContext context => context,
                JsonElement je => je.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions),
                _ => null
            };
        }

        public void SetConversationContext(ConversationContext? context)
        {
            if (context is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[ConversationContextKey] = context;
        }

        public AgentConfigPatch? GetConfigPatch()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(ConfigPatchKey);
            return value switch
            {
                AgentConfigPatch patch => patch,
                JsonElement je => je.Deserialize<AgentConfigPatch>(ChannelProtocol.SerializerOptions),
                _ => null
            };
        }

        public void SetConfigPatch(AgentConfigPatch? patch)
        {
            if (patch is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[ConfigPatchKey] = patch;
        }

        // References, never bytes: this is what the persisted message carries, so a history read
        // costs the same whether or not files were sent (ADR 0020).
        public IReadOnlyList<AttachmentReference>? GetAttachments() =>
            GetList<AttachmentReference>(message, AttachmentsKey);

        public void SetAttachments(IReadOnlyList<AttachmentReference>? attachments) =>
            SetList(message, AttachmentsKey, attachments);

        // Which channel can still get the bytes. Hydration reads it to know who to ask; the
        // reference itself stays transport-neutral.
        public string? GetAttachmentChannelId()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(AttachmentChannelIdKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetAttachmentChannelId(string? channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[AttachmentChannelIdKey] = channelId;
        }

        // Where this turn's attachments landed in the agent's sandbox. Metadata rather than text
        // for the same reason the references are: the model is told on the way out, and the
        // transcript a person reads must not grow an internal path. It outlives hydration, so a
        // later turn can still act on the file after the bytes stop being sent.
        public IReadOnlyList<string>? GetSandboxPaths() => GetList<string>(message, SandboxPathsKey);

        public void SetSandboxPaths(IReadOnlyList<string>? paths) =>
            SetList(message, SandboxPathsKey, paths);

        // The files this turn could not put in the sandbox, by the names the person used. Recorded
        // beside the landed paths so the turn's record is complete, but spoken only within the
        // hydration distance: past it the model has neither the bytes nor the file, and a notice
        // about neither is noise.
        public IReadOnlyList<string>? GetLandingFailures() =>
            GetList<string>(message, LandingFailuresKey);

        public void SetLandingFailures(IReadOnlyList<string>? fileNames) =>
            SetList(message, LandingFailuresKey, fileNames);
    }

    // The three list-valued properties are read and written the same way: the list itself on the
    // turn that set it, a JSON element on a turn that read the message back from the store, and an
    // empty list recorded as nothing at all rather than as an empty entry.
    private static IReadOnlyList<T>? GetList<T>(ChatMessage message, string key) =>
        message.AdditionalProperties?.GetValueOrDefault(key) switch
        {
            IReadOnlyList<T> list => list,
            JsonElement je => je.Deserialize<IReadOnlyList<T>>(ChannelProtocol.SerializerOptions),
            _ => null
        };

    private static void SetList<T>(ChatMessage message, string key, IReadOnlyList<T>? values)
    {
        if (values is null or { Count: 0 })
        {
            return;
        }

        message.AdditionalProperties ??= [];
        message.AdditionalProperties[key] = values;
    }

    extension(ChatResponseUpdate update)
    {
        public void SetTimestamp(DateTimeOffset timestamp)
        {
            update.AdditionalProperties ??= [];
            update.AdditionalProperties[TimestampKey] = timestamp;
        }
    }

    extension(AgentResponseUpdate update)
    {
        public DateTimeOffset? GetTimestamp()
        {
            return ParseTimestamp(update.AdditionalProperties?.GetValueOrDefault(TimestampKey));
        }
    }

    private static DateTimeOffset? ParseTimestamp(object? value)
    {
        return value switch
        {
            DateTimeOffset dto => dto,
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je
                when DateTimeOffset.TryParse(je.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}