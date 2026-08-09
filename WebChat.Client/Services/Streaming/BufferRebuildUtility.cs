using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

public record BufferResumeResult(
    List<ChatMessageModel> MergedMessages,
    ChatMessageModel StreamingMessage);

public static class BufferRebuildUtility
{
    public static BufferResumeResult ResumeFromBuffer(
        IReadOnlyList<ChatStreamMessage> buffer,
        IReadOnlyList<ChatMessageModel> existingHistory,
        string? currentPrompt,
        string? currentSenderId)
    {
        var historyById = existingHistory
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .ToDictionary(m => m.MessageId!, m => m);

        var (completedTurns, rawStreamingMessage) = RebuildFromBuffer(buffer);

        var historyContent = existingHistory
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();
        var streamingMessage = StripKnownContent(rawStreamingMessage, historyContent);

        // Classify completed turns: anchors (MessageId in history) track position,
        // new messages are grouped by which anchor they follow
        string? lastAnchorId = null;
        var followingNew = new Dictionary<string, List<ChatMessageModel>>();
        var leadingNew = new List<ChatMessageModel>();

        // Exclude user messages from the merge loop: they lack MessageIds in the buffer
        // so they can't be anchored, and since the server already persists them in history
        // they'd be incorrectly treated as "new" and duplicated on each reconnection.
        // The current prompt is handled separately below (lines 102-112).
        foreach (var turn in completedTurns.Where(t =>
                     t.HasContent && t.Role != "user"))
        {
            if (!string.IsNullOrEmpty(turn.MessageId) && historyById.ContainsKey(turn.MessageId))
            {
                followingNew[turn.MessageId] = [];
                lastAnchorId = turn.MessageId;
            }
            else if (lastAnchorId is not null)
            {
                followingNew[lastAnchorId].Add(turn);
            }
            else
            {
                leadingNew.Add(turn);
            }
        }

        // Build merged list: walk history, enrich anchors, insert new messages at anchor positions
        var merged = new List<ChatMessageModel>(existingHistory.Count + completedTurns.Count);
        var leadingInserted = false;
        var completedById = completedTurns
            .Where(t => !string.IsNullOrEmpty(t.MessageId))
            .ToDictionary(t => t.MessageId!, t => t);

        foreach (var msg in existingHistory)
        {
            // Insert leading new messages before the first anchor
            if (!leadingInserted && msg.MessageId is not null && followingNew.ContainsKey(msg.MessageId))
            {
                merged.AddRange(leadingNew);
                leadingInserted = true;
            }

            // Enrich anchor with buffer reasoning/toolcalls, or pass through unchanged
            if (msg.MessageId is not null && completedById.TryGetValue(msg.MessageId, out var anchorTurn))
            {
                var needsReasoning = string.IsNullOrEmpty(msg.Reasoning) && !string.IsNullOrEmpty(anchorTurn.Reasoning);
                var needsToolCalls = string.IsNullOrEmpty(msg.ToolCalls) && !string.IsNullOrEmpty(anchorTurn.ToolCalls);
                merged.Add((needsReasoning || needsToolCalls)
                    ? msg with
                    {
                        Reasoning = needsReasoning ? anchorTurn.Reasoning : msg.Reasoning,
                        ToolCalls = needsToolCalls ? anchorTurn.ToolCalls : msg.ToolCalls
                    }
                    : msg);
            }
            else
            {
                merged.Add(msg);
            }

            // Insert new messages that follow this anchor
            if (msg.MessageId is not null && followingNew.TryGetValue(msg.MessageId, out var following))
            {
                merged.AddRange(following);
            }
        }

        // Add current prompt before unanchored buffer content so the user message
        // appears before the assistant responses it triggered (fixes ordering on
        // page refresh while the agent is still responding).
        if (!string.IsNullOrEmpty(currentPrompt) &&
            !existingHistory.Any(m => m.Role == "user" && m.Content == currentPrompt))
        {
            merged.Add(new ChatMessageModel
            {
                Role = "user",
                Content = currentPrompt,
                SenderId = currentSenderId
            });
        }

        // Append leading new if no anchors were found
        if (!leadingInserted)
        {
            merged.AddRange(leadingNew);
        }

        return new BufferResumeResult(merged, streamingMessage);
    }

    internal static (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages)
    {
        var completedTurns = new List<ChatMessageModel>();
        var currentAssistantMessage = new ChatMessageModel { Role = "assistant" };

        if (bufferedMessages.Count == 0)
        {
            return (completedTurns, currentAssistantMessage);
        }

        var ordered = bufferedMessages.OrderBy(m => m.SequenceNumber).ToList();
        string? currentMessageId = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var msg = ordered[i];

            if (msg.UserMessage is not null)
            {
                currentAssistantMessage = FinalizeAssistantTurn(completedTurns, currentAssistantMessage);

                completedTurns.Add(new ChatMessageModel
                {
                    Role = "user",
                    Content = msg.Content ?? "",
                    SenderId = msg.UserMessage.SenderId,
                    Timestamp = msg.UserMessage.Timestamp,
                    Attachments = msg.Attachments
                });
                continue;
            }

            if (currentMessageId is not null && msg.MessageId != currentMessageId)
            {
                currentAssistantMessage = FinalizeAssistantTurn(completedTurns, currentAssistantMessage);
            }

            currentMessageId = msg.MessageId;

            if (!string.IsNullOrEmpty(msg.Content) ||
                !string.IsNullOrEmpty(msg.Reasoning) ||
                !string.IsNullOrEmpty(msg.ToolCalls))
            {
                currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg);
            }

            if (!msg.IsComplete)
            {
                continue;
            }

            // Only finalize on IsComplete when the next assistant chunk has a different
            // MessageId (or there is none). This matches streaming, which accumulates all
            // chunks for the same MessageId into a single message regardless of IsComplete.
            var nextMessageId = NextAssistantMessageId(ordered, i);
            if (nextMessageId is null || nextMessageId != currentMessageId)
            {
                currentAssistantMessage = FinalizeAssistantTurn(completedTurns, currentAssistantMessage);
            }
        }

        return (completedTurns, currentAssistantMessage);
    }

    private static string? NextAssistantMessageId(List<ChatStreamMessage> ordered, int currentIndex)
    {
        for (var j = currentIndex + 1; j < ordered.Count; j++)
        {
            if (ordered[j].UserMessage is null)
            {
                return ordered[j].MessageId;
            }
        }

        return null;
    }

    private static ChatMessageModel FinalizeAssistantTurn(
        List<ChatMessageModel> completedTurns,
        ChatMessageModel currentMessage)
    {
        if (currentMessage.HasContent)
        {
            completedTurns.Add(currentMessage);
        }

        return new ChatMessageModel { Role = "assistant" };
    }

    private static ChatMessageModel StripKnownContent(ChatMessageModel message, HashSet<string> historyContent)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            return message;
        }

        if (historyContent.Any(known => known.Contains(message.Content)))
        {
            return message with { Content = "" };
        }

        var content = message.Content;
        foreach (var known in historyContent.Where(known => content.StartsWith(known)))
        {
            content = content[known.Length..].TrimStart();
        }

        return content != message.Content ? message with { Content = content } : message;
    }

    internal static ChatMessageModel AccumulateChunk(
        ChatMessageModel streamingMessage,
        ChatStreamMessage chunk)
    {
        if (!string.IsNullOrEmpty(chunk.MessageId) && streamingMessage.MessageId != chunk.MessageId)
        {
            streamingMessage = streamingMessage with { MessageId = chunk.MessageId };
        }

        if (!string.IsNullOrEmpty(chunk.Content))
        {
            streamingMessage = streamingMessage with
            {
                Content = string.IsNullOrEmpty(streamingMessage.Content)
                    ? chunk.Content
                    : streamingMessage.Content + chunk.Content
            };
        }

        if (!string.IsNullOrEmpty(chunk.Reasoning))
        {
            streamingMessage = streamingMessage with
            {
                Reasoning = string.IsNullOrEmpty(streamingMessage.Reasoning)
                    ? chunk.Reasoning
                    : streamingMessage.Reasoning + chunk.Reasoning
            };
        }

        if (!string.IsNullOrEmpty(chunk.ToolCalls))
        {
            streamingMessage = streamingMessage with
            {
                ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                    ? chunk.ToolCalls
                    : streamingMessage.ToolCalls + "\n" + chunk.ToolCalls
            };
        }

        if (chunk.Timestamp is not null)
        {
            streamingMessage = streamingMessage with { Timestamp = chunk.Timestamp };
        }

        return streamingMessage;
    }
}