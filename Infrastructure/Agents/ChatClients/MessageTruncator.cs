using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    private const int PerMessageOverhead = 4;
    private const int OtherContentOverhead = 4;
    private const int PerToolOverhead = 4;
    private const double SafetyRatio = 0.95;

    // What an image costs whatever its file size, and how much of a document one token stands for.
    // Both come from the range the providers publish rather than a measurement of any one file;
    // the point is that a 1 MB PDF stops counting as four tokens.
    //
    // The ceiling is the load-bearing part. A document's file size tracks its images far more
    // than its text, so a 20 MB scan would otherwise estimate past the whole context window and
    // make the truncator drop every earlier message to make room for it. ADR 0020 puts a heavy
    // PDF at about 50,000 tokens, so that is where an attachment's estimate stops.
    private const int ImageTokens = 1_500;
    private const int DocumentBytesPerToken = 50;
    private const int MaxAttachmentTokens = 50_000;

    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static int EstimateOptionsOverheadTokens(ChatOptions? options)
    {
        if (options is null)
        {
            return 0;
        }

        var instructionsTokens = EstimateTokens(options.Instructions ?? string.Empty);
        var toolsTokens = (options.Tools ?? Enumerable.Empty<AITool>())
            .Sum(EstimateToolTokens);
        return instructionsTokens + toolsTokens;
    }

    private static int EstimateToolTokens(AITool tool)
    {
        var schemaTokens = tool is AIFunction fn
            ? EstimateTokens(fn.JsonSchema.GetRawText())
            : 0;
        return EstimateTokens(tool.Name)
            + EstimateTokens(tool.Description ?? string.Empty)
            + schemaTokens
            + PerToolOverhead;
    }

    public static IReadOnlyList<ChatMessage> Truncate(
        IReadOnlyList<ChatMessage> messages,
        int? maxContextTokens,
        out int droppedCount,
        out int tokensBefore,
        out int tokensAfter,
        out bool overflowDetected,
        int fixedOverheadTokens = 0)
    {
        droppedCount = 0;
        overflowDetected = false;
        var tokenEstimates = messages.Select(EstimateMessageTokens).ToArray();
        var messageTokens = tokenEstimates.Sum();
        tokensBefore = messageTokens + fixedOverheadTokens;
        tokensAfter = tokensBefore;

        if (maxContextTokens is null or <= 0 || messages.Count == 0)
        {
            return messages;
        }

        var threshold = (int)Math.Floor(maxContextTokens.Value * SafetyRatio);
        if (tokensBefore <= threshold)
        {
            return messages;
        }

        overflowDetected = true;

        var lastUserIndex = LastIndexOfRole(messages, ChatRole.User);
        var pinned = Enumerable.Range(0, messages.Count)
            .Where(i => messages[i].Role == ChatRole.System || i == lastUserIndex)
            .ToHashSet();

        var groups = BuildDropGroups(messages, pinned);

        var kept = Enumerable.Range(0, messages.Count).ToHashSet();
        var currentTokens = tokensBefore;

        foreach (var group in groups)
        {
            if (currentTokens <= threshold)
            {
                break;
            }

            currentTokens -= group.Sum(i => tokenEstimates[i]);
            kept.ExceptWith(group);
            droppedCount += group.Count;
        }

        tokensAfter = currentTokens;
        return Enumerable.Range(0, messages.Count)
            .Where(kept.Contains)
            .Select(i => messages[i])
            .ToList();
    }

    // Single forward pass: each tool-result message joins the group of its matching
    // FunctionCall via a callId→groupIndex map, so this is O(n) instead of O(n²).
    private static IReadOnlyList<IReadOnlyList<int>> BuildDropGroups(
        IReadOnlyList<ChatMessage> messages, HashSet<int> pinned)
    {
        var groups = new List<List<int>>();
        var callIdToGroupIdx = new Dictionary<string, int>();

        for (var i = 0; i < messages.Count; i++)
        {
            if (pinned.Contains(i))
            {
                continue;
            }

            var msg = messages[i];
            var matchingGroupIdx = msg.Contents
                .OfType<FunctionResultContent>()
                .Select(r => callIdToGroupIdx.TryGetValue(r.CallId, out var gi) ? gi : -1)
                .FirstOrDefault(idx => idx >= 0, -1);

            if (matchingGroupIdx >= 0)
            {
                groups[matchingGroupIdx].Add(i);
                continue;
            }

            var newGroupIdx = groups.Count;
            groups.Add([i]);
            foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            {
                callIdToGroupIdx[fc.CallId] = newGroupIdx;
            }
        }

        return groups;
    }

    private static int LastIndexOfRole(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == role)
            {
                return i;
            }
        }
        return -1;
    }

    public static int EstimateMessageTokens(ChatMessage message)
    {
        var contentTokens = message.Contents.Sum(EstimateContentTokens);
        return contentTokens + PerMessageOverhead;
    }

    private static int EstimateContentTokens(AIContent content) => content switch
    {
        TextContent t => EstimateTokens(t.Text),
        TextReasoningContent r => EstimateTokens(r.Text),
        FunctionCallContent fc => EstimateTokens(JsonSerializer.Serialize(
            new { name = fc.Name, arguments = fc.Arguments })),
        FunctionResultContent fr => EstimateTokens(JsonSerializer.Serialize(fr.Result)),
        DataContent d => EstimateAttachmentTokens(d),
        _ => OtherContentOverhead
    };

    // Without a case here an attachment counts as a fixed handful of tokens and truncation goes
    // blind on a large document. The two kinds do not scale the same way: a provider resizes an
    // image into its own tile scheme before billing, so the file's size says almost nothing, while
    // a document is billed on what it parses to, which does track its size — up to the ceiling.
    private static int EstimateAttachmentTokens(DataContent content)
    {
        return content.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            ? ImageTokens
            : (int)Math.Min(MaxAttachmentTokens, content.Data.Length / (long)DocumentBytesPerToken);
    }
}