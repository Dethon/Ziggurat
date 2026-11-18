using Domain.DTOs.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

// Line-numbered read result matching the Sandbox/Vault file_read shape, for the home's rendered
// files and its watches alike.
public static class HaReadResult
{
    public static FsResult<FsReadResult> Numbered(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));
        var truncated = take < remaining.Length;

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = filePath,
            Content = content,
            TotalLines = allLines.Length,
            Truncated = truncated,
            Suggestion = truncated ? $"Use offset={start + take + 1} to continue reading." : null
        });
    }
}