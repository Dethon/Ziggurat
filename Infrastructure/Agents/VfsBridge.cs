using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Outposts;
using Domain.Tools.FileSystem.Bridge;

namespace Infrastructure.Agents;

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
public sealed class VfsBridge(TimeProvider time, VfsBridgeSettings settings) : IVfsBridge
{
    // A backstop, not the bound: every exec completes its token in a finally. This only reclaims a
    // token whose exec never returned, and outlasts the longest timeout a sandbox accepts.
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    // When a token stops answering is the registry's to know, not the call's: a call never reads it.
    private readonly ConcurrentDictionary<string, (VfsCall Call, DateTimeOffset Expires)> _calls =
        new(StringComparer.Ordinal);

    public VfsCall Mint(IVirtualFileSystemRegistry registry, Func<string, bool> permits, ConversationContext? caller)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var call = new VfsCall(
            token, registry, permits, [.. registry.GetMounts().Where(VfsCall.IsServed)], caller, settings.MaxFileBytes);
        _calls[token] = (call, time.GetUtcNow() + Lifetime);
        return call;
    }

    public VfsCall? Find(string token)
    {
        if (!_calls.TryGetValue(token, out var held))
        {
            return null;
        }

        if (time.GetUtcNow() < held.Expires)
        {
            return held.Call;
        }

        _calls.TryRemove(token, out _);
        return null;
    }

    public void Revoke(string token)
    {
        if (_calls.TryGetValue(token, out var held))
        {
            held.Call.Revoke();
        }
    }

    public VfsCallRecord Complete(string token) =>
        _calls.TryRemove(token, out var held)
            ? new VfsCallRecord(held.Call.Changes, held.Call.Truncated)
            : VfsCallRecord.Empty;
}