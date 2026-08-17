using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// What file_read answers for an image: a small envelope, never the bytes — those cannot travel on a
// tool message at all (ADR 0029). A distinct shape from the text read's rather than its fields
// repurposed, because it makes distinct claims: there are no lines to number and no window to page
// through, and there is one claim a text read never makes — whether the model is about to be shown
// the picture.
//
// `Shown: false` always carries a Note saying which reason applied, so the model can tell an honest
// failure from a picture it simply has not looked at yet.
public sealed record FsImageReadResult
{
    public required string FilePath { get; init; }
    public required string MediaType { get; init; }
    public required long SizeBytes { get; init; }
    public required bool Shown { get; init; }
    public string? Note { get; init; }

    // The send that puts bytes in front of the model reads the envelope back out of the tool result
    // to learn which path an image came from, so it can name that path in a placeholder once the
    // bytes are gone. Strict on purpose: only this exact shape parses, so a different tool's result
    // that happens to carry a path cannot be mistaken for an image read.
    public static FsImageReadResult? TryRead(object? result)
    {
        try
        {
            var node = result as JsonNode ?? JsonSerializer.SerializeToNode(result);
            return node?.Deserialize<FsImageReadResult>(FsResultContract.ValidationOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}