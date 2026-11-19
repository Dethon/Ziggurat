namespace Domain.Contracts;

// One rendered version of a file per read of it. A mount that renders its files on every call (the
// /sugary tables, rebuilt from the database) is read in chunks, one call each, with nothing in a call
// saying which read it belongs to; rendered afresh per chunk, a change landing between two calls tears
// the file — the first half of one version and the end of another, which a Parquet reader takes for a
// broken file or, worse, reads. So each read's chunks come from the version it began on, an info and
// the read that follows it agree, and a read begun after another finished renders the file again.
public interface IReadSnapshots
{
    // What an info reports: the version a read starting now would get.
    Task<HeldBytes> PeekAsync(string key, Func<CancellationToken, Task<byte[]?>> render, CancellationToken ct);

    // The bytes a call reading [offset, offset + length) of the file under key is served. From the start,
    // a version an info just rendered and no read has finished yet, else a fresh render; past the start,
    // the version of the read that stopped exactly there. Bytes null is no such file, and nothing is held.
    // Lost is a read past the start whose version is gone (it stalled past the time one is kept): it
    // cannot be finished from another, so it is to start again.
    Task<HeldBytes> TakeAsync(
        string key, long offset, long length, Func<CancellationToken, Task<byte[]?>> render, CancellationToken ct);
}

public sealed record HeldBytes(byte[]? Bytes, bool Lost = false);