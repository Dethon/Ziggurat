using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

// Applying a list of text edits to a rendered file, for the virtual mounts whose "file" is a
// record rendered on read: the schedule, the print queue and the watch each had their own copy of
// this. One edit replaces its first occurrence, or every one when it says so, and reports how many
// it touched — an edit whose text is not there is the caller's error, said by name.
public static class TextEdits
{
    public sealed record Applied(string Text, IReadOnlyList<FsEditDetail> Details)
    {
        public int Total => Details.Sum(d => d.OccurrencesReplaced);
    }

    public static FsResult<Applied> Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var details = new List<FsEditDetail>();
        foreach (var edit in edits)
        {
            var count = Count(text, edit.OldString);
            if (count == 0)
            {
                return FsError.Invalid<Applied>($"Text not found: '{edit.OldString}'");
            }

            var replaced = edit.ReplaceAll ? count : 1;
            text = ReplaceFirstOrAll(text, edit.OldString, edit.NewString, edit.ReplaceAll);
            // A rendered record has no stable line to name, so the range is the whole file.
            details.Add(new FsEditDetail { OccurrencesReplaced = replaced, AffectedLines = new FsLineRange { Start = 1, End = text.Split('\n').Length } });
        }

        return new FsResult<Applied>.Ok(new Applied(text, details));
    }

    private static int Count(string text, string value) =>
        value.Length == 0 ? 0 : (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    public static string ReplaceFirstOrAll(string text, string oldValue, string newValue, bool all)
    {
        if (all)
        {
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}