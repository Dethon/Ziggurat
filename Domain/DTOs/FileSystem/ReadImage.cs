namespace Domain.DTOs.FileSystem;

// One image the model asked to see. The path is the virtual one the caller spelled, because that
// is what the model has to name again if the bytes are gone by the time it looks — the file never
// left the mount.
public sealed record ReadImage
{
    public required string VirtualPath { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Bytes { get; init; }
}