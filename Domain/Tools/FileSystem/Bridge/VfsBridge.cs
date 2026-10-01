using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Outposts;

namespace Domain.Tools.FileSystem.Bridge;

// The agent's half of "every mount is a filesystem to the shell": a sandbox command's file
// operations arrive here, one request each, from the FUSE daemon serving the mounts at /vfs inside
// the command's namespace. Meaning lives here, not in the daemon: every operation is resolved
// through the calling session's registry and allowed only where the equivalent file tool call
// would run unasked, so the shell is one more client of the rules the tools obey.
//
// A call token is the only way in. The exec tool mints one per call, bound to its session's
// registry, its approval context and the mounts it serves; it is revoked when exec returns, is
// cancelled or times out. Minting, revocation and lookup are explicit calls: there is no ambient
// "current call" anywhere on this path.
public sealed class VfsBridge(TimeProvider time)
{
    // A backstop, not the bound: every exec completes its token in a finally. This only reclaims a
    // token whose exec never returned, and outlasts the longest timeout a sandbox accepts.
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, VfsCall> _calls = new(StringComparer.Ordinal);

    public VfsCall Mint(IVirtualFileSystemRegistry registry, Func<string, bool> permits)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var call = new VfsCall(token, registry, permits, Served(registry), time.GetUtcNow() + Lifetime);
        _calls[token] = call;
        return call;
    }

    // Null for a token never minted, completed or expired: all three are a caller with no call.
    public VfsCall? Find(string token)
    {
        if (!_calls.TryGetValue(token, out var call))
        {
            return null;
        }

        if (time.GetUtcNow() < call.Expires)
        {
            return call;
        }

        _calls.TryRemove(token, out _);
        return null;
    }

    // The command is being killed or the call cancelled: whatever arrives from now on is logged as
    // dropped rather than applied, and the token still answers so the drops are recorded.
    public void Revoke(string token)
    {
        if (_calls.TryGetValue(token, out var call))
        {
            call.Revoke();
        }
    }

    // The exec returned: the token stops answering, and what it recorded is the result's.
    public VfsCallRecord Complete(string token) =>
        _calls.TryRemove(token, out var call) ? new VfsCallRecord(call.Changes, call.Truncated) : VfsCallRecord.Empty;

    // Every mount the session has except an outpost — a separate machine, never reachable from the
    // sandbox — and the shell's own disk, which the command already has.
    private static IReadOnlyList<FileSystemMount> Served(IVirtualFileSystemRegistry registry) =>
        [.. registry.GetMounts().Where(m => !OutpostMountPoint.Addresses(m.MountPoint) && m.ShellReach is null)];
}
// What a call recorded: the changes it made through the mounts, and the directories a walk saw
// only part of.
public sealed record VfsCallRecord(IReadOnlyList<VfsChange> Changes, IReadOnlyList<string> Truncated)
{
    public static readonly VfsCallRecord Empty = new([], []);
}