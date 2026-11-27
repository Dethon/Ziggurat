using System.Text.RegularExpressions;
using WebChat.Client.Helpers;
using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

// A turn on the rail: the row it starts at, what was asked and the reply's words, as the page holds them.
public sealed record TurnRailItem(int RowIndex, string? Asked, string? Answered);

// The rail beside the conversation (TurnRailView), one strip per turn. A turn is a message to the agent —
// a person's, or a line a service started — and every row up to the next one, so a long reply keeps its
// strip lit and a jump lands on the question rather than somewhere in its answer.
public static partial class TurnRail
{
    // How much of a message a preview reads: it shows a few lines, and a long reply streaming in would
    // otherwise be parsed whole on every hover.
    public const int PreviewSource = 1_500;

    public const int MinItems = 2;

    // Strips are 2px tall; centres closer than this merge into one bar once layout rounds them.
    private const double MinMarkSpacing = 5;
    private const int ItemSpacing = 8;

    public static IReadOnlyList<TurnRailItem> Items(IReadOnlyList<ChatMessageModel> rows)
    {
        var starts = rows.Select((row, index) => (row, index))
            .Where(r => r.row.Role == "user")
            .Select(r => r.index)
            .ToList();

        return starts.Select((start, i) =>
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : rows.Count;
            var answer = rows.Skip(start + 1).Take(end - start - 1)
                .LastOrDefault(r => r is { Role: "assistant", IsError: false } && !string.IsNullOrWhiteSpace(r.Content));
            return new TurnRailItem(start, ServiceNotice.Of(rows[start]) ?? rows[start].Content, answer?.Content);
        }).ToList();
    }

    public static string? Preview(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var cut = markdown.Length <= PreviewSource ? markdown.Length
            : char.IsHighSurrogate(markdown[PreviewSource - 1]) ? PreviewSource - 1 : PreviewSource;
        var text = Whitespace().Replace(ChatMarkdown.ToPlainText(markdown[..cut]), " ").Trim();
        return text.Length > 0 ? text : null;
    }

    // The rail is as tall as its turns spaced 8px apart, but never taller than the conversation leaves
    // room for, with space above and below for the end turns' previews.
    public static string HeightStyle(int itemCount) =>
        $"min({Math.Max(1, (itemCount - 1) * ItemSpacing)}px, max(4rem, calc(100% - 9rem)))";

    public static int MarkCount(int itemCount, double railHeight) =>
        railHeight <= 0
            ? itemCount
            : Math.Min(itemCount, Math.Max(2, (int)Math.Floor(railHeight / MinMarkSpacing) + 1));

    public static int MarkOf(int itemIndex, int itemCount, int markCount) =>
        markCount >= itemCount
            ? itemIndex
            : (int)Math.Round((double)itemIndex / (itemCount - 1) * (markCount - 1), MidpointRounding.AwayFromZero);

    public static double TopPercent(int index, int count) =>
        count <= 1 ? 0 : (double)Math.Clamp(index, 0, count - 1) / (count - 1) * 100;

    public static int? IndexAt(double offsetY, double railHeight, int itemCount)
    {
        if (itemCount <= 0 || railHeight <= 0)
        {
            return null;
        }

        var progress = Math.Clamp(offsetY / railHeight, 0, 1);
        return (int)Math.Round(progress * (itemCount - 1), MidpointRounding.AwayFromZero);
    }

    // How wide a strip draws beside the one the pointer is on: a fisheye, so the turn being pointed at
    // stands out from its neighbours without the rail growing.
    public static double Swell(int? distance) => distance switch
    {
        0 => 1.0,
        1 => 0.667,
        2 => 0.417,
        _ => 0.333
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}