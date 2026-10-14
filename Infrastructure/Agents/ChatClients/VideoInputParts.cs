using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Infrastructure.Agents.ChatClients;

// A clip goes on the Responses wire as OpenRouter's `input_video` part, its bytes a data URL. The
// adapter has no video part of its own and drops a video DataContent without a word, so each one is
// handed over as the raw part instead, which the adapter passes through untouched. Done last, on the
// copy that is sent and thrown away: hydration and truncation keep seeing an ordinary DataContent.
internal static class VideoInputParts
{
    public static IReadOnlyList<ChatMessage> AsSent(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(m => m.Contents.Any(IsVideo)) ? messages.Select(AsSent).ToList() : messages;

    private static ChatMessage AsSent(ChatMessage message)
    {
        if (!message.Contents.Any(IsVideo))
        {
            return message;
        }

        var copy = message.Clone();
        copy.Contents = [.. message.Contents.Select(c => IsVideo(c) ? ToPart((DataContent)c) : c)];
        return copy;
    }

    private static bool IsVideo(AIContent content) =>
        content is DataContent data && AttachmentKinds.IsVideo(data.MediaType);

    private static AIContent ToPart(DataContent clip)
    {
        var json = new JsonObject
        {
            ["type"] = "input_video",
            ["video_url"] = clip.Uri
        };
        return new AIContent
        {
            RawRepresentation = ModelReaderWriter.Read<ResponseContentPart>(BinaryData.FromString(json.ToJsonString()))
        };
    }
}