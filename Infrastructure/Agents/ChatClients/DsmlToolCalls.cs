using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

// DeepSeek now and then writes a tool call as text in its own markup (DSML) instead of calling —
// seen on prod through Baidu and Novita and in the eval through Wafer and Together, so no provider
// pin avoids it. Left as text it ends the turn with the markup on screen; read back here as the
// calls it names, the turn goes on as if the model had called properly (user's decision,
// 2026-11-26).
//
// Words stream as they come until the opening marker; from there the rest of the reply is held,
// because only its end says whether it is one whole calls block. A whole block becomes the calls;
// anything else goes out as the text it was, at the end.
internal static partial class DsmlToolCalls
{
    private const string Marker = "<｜DSML｜";

    public static async IAsyncEnumerable<ChatResponseUpdate> RepairAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var carry = "";
        StringBuilder? heldText = null;
        List<ChatResponseUpdate> held = [];
        ChatResponseUpdate? last = null;

        await foreach (var update in updates.WithCancellation(ct))
        {
            last = update;
            var text = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
            if (heldText is not null)
            {
                heldText.Append(text);
                held.Add(Without(update, text: null));
                continue;
            }

            if (text.Length == 0 && carry.Length == 0)
            {
                yield return update;
                continue;
            }

            var combined = carry + text;
            var at = combined.IndexOf(Marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                heldText = new StringBuilder(combined[at..]);
                held.Add(Without(update, text: null));
                if (at > 0)
                {
                    yield return TextOnly(update, combined[..at]);
                }

                carry = "";
                continue;
            }

            // A stream may cut the marker itself: what could still begin it waits for the next piece.
            var cut = PartialMarkerLength(combined);
            carry = combined[^cut..];
            yield return Without(update, text: combined[..^cut]);
        }

        if (last is null)
        {
            yield break;
        }

        if (heldText is null)
        {
            if (carry.Length > 0)
            {
                yield return TextOnly(last, carry);
            }

            yield break;
        }

        // The words or the calls first, then what else the held updates carried (usage, the
        // finish), which for calls says the reply ended in them.
        var calls = Parse(heldText.ToString());
        yield return calls is null
            ? TextOnly(last, heldText.ToString())
            : Finished(Copy(last, [.. calls]), ChatFinishReason.ToolCalls);

        foreach (var update in held.Where(u => u.Contents.Count > 0 || u.FinishReason is not null))
        {
            yield return calls is not null && update.FinishReason is not null
                ? Finished(update, ChatFinishReason.ToolCalls)
                : update;
        }
    }

    // The calls a whole block names, or null where the text is anything else.
    private static IReadOnlyList<FunctionCallContent>? Parse(string text)
    {
        var block = BlockPattern().Match(text);
        if (!block.Success)
        {
            return null;
        }

        var body = block.Groups["body"].Value;
        var invokes = InvokePattern().Matches(body);
        if (invokes.Count == 0 || InvokePattern().Replace(body, "").Trim().Length > 0)
        {
            return null;
        }

        return invokes
            .Select(invoke =>
            {
                var parameters = ParameterPattern().Matches(invoke.Groups["body"].Value);
                return new FunctionCallContent(
                    $"call_{Guid.NewGuid():N}",
                    invoke.Groups["name"].Value,
                    parameters.ToDictionary(p => p.Groups["name"].Value, Value));
            })
            .ToList();
    }

    // string="true" is the text as written; string="false" is a JSON value, kept as written when it
    // will not parse rather than losing the call.
    private static object? Value(Match parameter)
    {
        var raw = parameter.Groups["value"].Value;
        if (parameter.Groups["string"].Value != "false")
        {
            return raw;
        }

        try
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static int PartialMarkerLength(string text) =>
        Enumerable.Range(1, Math.Min(Marker.Length - 1, text.Length))
            .Reverse()
            .FirstOrDefault(n => text.EndsWith(Marker[..n], StringComparison.Ordinal));

    private static ChatResponseUpdate TextOnly(ChatResponseUpdate source, string text) =>
        Finished(Copy(source, [new TextContent(text)]), null);

    // The update with its text replaced (or dropped, for null or empty), everything else it carried kept.
    private static ChatResponseUpdate Without(ChatResponseUpdate source, string? text)
    {
        var others = source.Contents.Where(c => c is not TextContent).ToList();
        return Copy(source, string.IsNullOrEmpty(text) ? others : [new TextContent(text), .. others]);
    }

    private static ChatResponseUpdate Copy(ChatResponseUpdate source, IList<AIContent> contents) => new()
    {
        AuthorName = source.AuthorName,
        Role = source.Role ?? ChatRole.Assistant,
        Contents = contents,
        RawRepresentation = source.RawRepresentation,
        AdditionalProperties = source.AdditionalProperties,
        ResponseId = source.ResponseId,
        MessageId = source.MessageId,
        ConversationId = source.ConversationId,
        CreatedAt = source.CreatedAt,
        FinishReason = source.FinishReason,
        ModelId = source.ModelId
    };

    private static ChatResponseUpdate Finished(ChatResponseUpdate update, ChatFinishReason? finish)
    {
        update.FinishReason = finish;
        return update;
    }

    [GeneratedRegex(@"\A\s*<｜DSML｜\s*(?:function_)?calls\s*>(?<body>.*)</｜DSML｜\s*(?:function_)?calls\s*>\s*\z", RegexOptions.Singleline)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"<｜DSML｜\s*invoke\s+name=""(?<name>[^""]+)""\s*>(?<body>.*?)</｜DSML｜\s*invoke\s*>", RegexOptions.Singleline)]
    private static partial Regex InvokePattern();

    [GeneratedRegex(@"<｜DSML｜\s*parameter\s+name=""(?<name>[^""]+)""(?:\s+string=""(?<string>true|false)"")?\s*>(?<value>.*?)</｜DSML｜\s*parameter\s*>", RegexOptions.Singleline)]
    private static partial Regex ParameterPattern();
}