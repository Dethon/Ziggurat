using System.Collections.Concurrent;
using Domain.Contracts;

namespace McpServerSugary.Tables;

// The versions of /sugary's files being read. A read in chunks asks for each next chunk at exactly the
// offset its last one ended, so a version knows its reads by where each stopped (its cursors): a call
// past the start is served the version with a read stopped at that offset, the newest if two are, and
// moves that read on. Two reads of one file at once each keep their own version, however the file
// changed between their starts. A version an info rendered is the next read's for a few seconds, so
// the size it reported is the size that read returns; once any read of it has finished, the next read
// renders the file again, so what was saved a moment ago is in it.
public sealed class ReadSnapshots(TimeProvider clock) : IReadSnapshots
{
    // An info and the read after it, a stat and its read: calls a moment apart.
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    // How long a read part way through may wait between two of its chunks. The proxy asks for the next
    // as soon as the last arrives, so this is generous; a read quiet for longer is told to start again.
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rendering = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Version>> _versions = new(StringComparer.Ordinal);

    public async Task<HeldBytes> PeekAsync(string key, Func<CancellationToken, Task<byte[]?>> render, CancellationToken ct) =>
        await StartAsync(key, render, version => new HeldBytes(version.Bytes), ct);

    public async Task<HeldBytes> TakeAsync(
        string key, long offset, long length, Func<CancellationToken, Task<byte[]?>> render, CancellationToken ct)
    {
        if (offset <= 0)
        {
            return await StartAsync(key, render, version => Serve(version, 0, length), ct);
        }

        lock (_gate)
        {
            Sweep();
            var version = _versions.GetValueOrDefault(key)?.LastOrDefault(v => v.Cursors.ContainsKey(offset));
            if (version is null)
            {
                return new HeldBytes(null, Lost: true);
            }

            version.Cursors.Remove(offset);
            return Serve(version, offset, length);
        }
    }

    // A version open to a read starting now, or one rendered for it; one render per file at a time.
    private async Task<HeldBytes> StartAsync(
        string key, Func<CancellationToken, Task<byte[]?>> render, Func<Version, HeldBytes> use, CancellationToken ct)
    {
        if (Open(key, use) is { } open)
        {
            return open;
        }

        var rendering = _rendering.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await rendering.WaitAsync(ct);
        try
        {
            // Another call may have rendered it while this one waited.
            if (Open(key, use) is { } rendered)
            {
                return rendered;
            }

            if (await render(ct) is not { } bytes)
            {
                return new HeldBytes(null);
            }

            var version = new Version(bytes, clock.GetUtcNow());
            lock (_gate)
            {
                Sweep();
                if (!_versions.TryGetValue(key, out var versions))
                {
                    _versions[key] = versions = [];
                }

                versions.Add(version);
                return use(version);
            }
        }
        finally
        {
            rendering.Release();
        }
    }

    private HeldBytes? Open(string key, Func<Version, HeldBytes> use)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            Sweep();
            return _versions.GetValueOrDefault(key)?.LastOrDefault() is { } newest && newest.OpenToStarts(now)
                ? use(newest)
                : null;
        }
    }

    // Called under the gate. A chunk that leaves more to read leaves a cursor where it ended; the last
    // chunk finishes the read, and a finished version takes no new read.
    private HeldBytes Serve(Version version, long offset, long length)
    {
        var end = offset + length;
        if (end < version.Bytes.Length)
        {
            version.Cursors[end] = clock.GetUtcNow();
        }
        else
        {
            version.Finished = true;
        }

        return new HeldBytes(version.Bytes);
    }

    // Called under the gate: a read quiet past Idle is let go, and a version nobody can use any more with it.
    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var versions in _versions.Values)
        {
            foreach (var version in versions)
            {
                foreach (var quiet in version.Cursors.Where(cursor => now >= cursor.Value + Idle).Select(cursor => cursor.Key).ToList())
                {
                    version.Cursors.Remove(quiet);
                }
            }

            versions.RemoveAll(version => version.Cursors.Count == 0 && !version.OpenToStarts(now));
        }

        foreach (var empty in _versions.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
        {
            _versions.Remove(empty);
        }
    }

    private sealed class Version(byte[] bytes, DateTimeOffset renderedAt)
    {
        public byte[] Bytes { get; } = bytes;

        // Where each read of this version in progress stopped, and when.
        public Dictionary<long, DateTimeOffset> Cursors { get; } = [];

        public bool Finished { get; set; }

        public bool OpenToStarts(DateTimeOffset now) => !Finished && now < renderedAt + Fresh;
    }
}