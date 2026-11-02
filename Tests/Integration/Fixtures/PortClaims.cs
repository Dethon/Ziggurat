using System.Collections.Concurrent;

namespace Tests.Integration.Fixtures;

// A port held across every test process on this machine, not only this one's pool: two runs side by
// side — a second checkout's suite, an agent's — each walk the same band, and a pool that knew only
// its own numbers handed both the same port, which the second then failed to bind with "address
// already in use" in a test that had nothing to do with ports. A claim is an exclusive lock on a file
// named after the port, which the system drops with the process that held it, so a run that dies
// leaves nothing claimed.
public sealed class PortClaims(string directory)
{
    public static readonly PortClaims Machine = new(Path.Combine(Path.GetTempPath(), "ziggurat-test-ports"));

    private readonly ConcurrentDictionary<int, FileStream> _held = new();

    public bool TryClaim(int port)
    {
        Directory.CreateDirectory(directory);
        try
        {
            var claim = new FileStream(
                Path.Combine(directory, $"{port}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (_held.TryAdd(port, claim))
            {
                return true;
            }

            claim.Dispose();
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Release(int port)
    {
        if (_held.TryRemove(port, out var claim))
        {
            claim.Dispose();
        }
    }
}