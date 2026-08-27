using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// What view_image answers for one picture on a page: the same envelope-without-bytes bargain
// FsImageReadResult makes, against a different kind of handle. A page image has no path — it has a
// ref that only means anything inside the session that issued it, and a label that is the only
// thing a person reading the note would recognise it by.
//
// A second shape rather than a widened first: the filesystem envelope is strict precisely so a
// result merely carrying a path cannot be mistaken for an image read, and loosening it to admit a
// pathless one would spend that. Hydration asks both and does not care which answered.
public sealed record PageImageResult
{
    public required string ImageRef { get; init; }

    // What the entry called it. Stands in for the path when the bytes are gone: the model chose
    // this picture by that name, so it is the name that identifies it afterwards.
    public required string Label { get; init; }

    public required string MediaType { get; init; }

    public long? SizeBytes { get; init; }

    public required bool Shown { get; init; }

    public string? Note { get; init; }

    public static PageImageResult? TryRead(object? result)
    {
        try
        {
            var node = result as JsonNode ?? JsonSerializer.SerializeToNode(result);
            return node?.Deserialize<PageImageResult>(FsResultContract.ValidationOptions);
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