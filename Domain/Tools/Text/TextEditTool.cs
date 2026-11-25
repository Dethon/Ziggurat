using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Description = """
                                         Edits a text file by applying a non-empty list of edits in order, atomically.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - edits: Ordered list of edits. Each edit has:
                                           - oldString: Exact text to find (case-sensitive)
                                           - newString: Replacement text
                                           - replaceAll: Replace all occurrences (default: false)

                                         Edits are applied sequentially against the running file contents — edit N sees the result of edits 1…N-1.
                                         If any edit fails (oldString not found, or multiple matches without replaceAll), the file is not written.

                                         When replaceAll is false, oldString must appear exactly once at that point in the sequence.
                                         If multiple occurrences are found, the tool fails — provide more surrounding context in oldString to disambiguate.

                                         Insert: include surrounding context in oldString, add new lines in newString.
                                         Delete: include content in oldString, omit it from newString.
                                         """;

    public FsResult<FsEditResult> Run(string filePath, IReadOnlyList<TextEdit> edits)
    {
        if (edits is null || edits.Count == 0)
        {
            return FsError.Invalid<FsEditResult>("edits must contain at least one entry.");
        }

        if (!ResolveExistingFile(filePath).TryGetValue(out var fullPath, out var resolveError))
        {
            return new FsResult<FsEditResult>.Err(resolveError);
        }

        if (!Apply(File.ReadAllText(fullPath), edits).TryGetValue(out var applied, out var applyError))
        {
            return new FsResult<FsEditResult>.Err(applyError);
        }

        var content = applied.Content;
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, fullPath, overwrite: true);

        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "success",
            FilePath = fullPath,
            TotalOccurrencesReplaced = applied.TotalReplaced,
            Edits = applied.Edits
        });
    }

    // The edits laid over a text in order, each seeing the result of the ones before it, or the first
    // that cannot apply; nothing is read or written. A mount that judges a file's content before
    // keeping it (an app manifest) applies here and stores only what passes.
    public static FsResult<AppliedEdits> Apply(string content, IReadOnlyList<TextEdit> edits)
    {
        var perEditResults = new List<FsEditDetail>();
        var totalReplaced = 0;

        foreach (var edit in edits)
        {
            var positions = FindAllOccurrences(content, edit.OldString);

            if (positions.Count == 0)
            {
                var suggestion = FindCaseInsensitiveSuggestion(content, edit.OldString);
                return FsError.Invalid<AppliedEdits>(suggestion is not null
                    ? $"Text '{Truncate(edit.OldString, 100)}' not found (case-sensitive). Did you mean '{Truncate(suggestion, 100)}'?"
                    : $"Text '{Truncate(edit.OldString, 100)}' not found in file.");
            }

            if (!edit.ReplaceAll && positions.Count > 1)
            {
                return FsError.Invalid<AppliedEdits>(
                    $"Found {positions.Count} occurrences of the specified text. Provide more surrounding context in oldString to disambiguate, or set replaceAll=true.");
            }

            var firstPosition = positions[0];
            content = edit.ReplaceAll
                ? content.Replace(edit.OldString, edit.NewString, StringComparison.Ordinal)
                : ReplaceFirst(content, edit.OldString, edit.NewString, firstPosition);

            var replacedCount = edit.ReplaceAll ? positions.Count : 1;
            totalReplaced += replacedCount;

            var (startLine, endLine) = ComputeAffectedLines(content, firstPosition, edit.NewString.Length);

            perEditResults.Add(new FsEditDetail
            {
                OccurrencesReplaced = replacedCount,
                AffectedLines = new FsLineRange { Start = startLine, End = endLine }
            });
        }

        return new FsResult<AppliedEdits>.Ok(new AppliedEdits(content, totalReplaced, perEditResults));
    }

    private static string ReplaceFirst(string content, string oldString, string newString, int position)
    {
        return content[..position] + newString + content[(position + oldString.Length)..];
    }

    private static List<int> FindAllOccurrences(string content, string searchText)
    {
        var positions = new List<int>();
        var index = 0;

        while ((index = content.IndexOf(searchText, index, StringComparison.Ordinal)) >= 0)
        {
            positions.Add(index);
            index += searchText.Length;
        }

        return positions;
    }

    private static string? FindCaseInsensitiveSuggestion(string content, string searchText)
    {
        var index = content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? content.Substring(index, searchText.Length) : null;
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int newLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var newTextContent = content.Substring(position, newLength);
        var linesInNew = newTextContent.Count(c => c == '\n');
        return (startLine, startLine + linesInNew);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}

public sealed record AppliedEdits(string Content, int TotalReplaced, IReadOnlyList<FsEditDetail> Edits);