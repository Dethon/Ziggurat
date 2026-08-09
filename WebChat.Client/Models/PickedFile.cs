namespace WebChat.Client.Models;

// A file as the platform's own picker handed it over, before anything has been read. The stream
// is opened lazily so a file refused at pick time is never read at all.
public sealed record PickedFile(
    string FileName,
    string MediaType,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenRead);