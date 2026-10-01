using Domain.Contracts;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;

namespace Agent.App;

// The exec bridge over HTTP: one POST per kernel operation, from the FUSE daemon inside a sandbox
// command's namespace. The call token is the only credential and the only thing this endpoint
// trusts — it names the session, its approval context and the mounts it serves — and an unknown,
// completed or expired one is answered 401. Reached on the deployment's own network only: the
// public proxy routes /api/agents* here and nothing else.
//
// The path is the daemon's view under /vfs, which is the virtual path the file tools take. A value
// answers 200 (JSON, or the raw bytes of a read); a refusal answers 422 with the errno the daemon
// hands the kernel and the mount's own envelope.
public static class VfsBridgeApi
{
    public const string Route = "/api/vfs-bridge";

    public static void MapVfsBridge(this IEndpointRouteBuilder app)
    {
        var bridge = app.MapGroup(Route);

        bridge.MapPost("/attr", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.AttrAsync(path, ct), a => new { kind = a.Kind, size = a.Size })));

        bridge.MapPost("/list", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.ListAsync(path, ct), l => new
            {
                entries = l.Entries.Select(e => new { name = e.Name, kind = e.Kind }),
                truncated = l.Truncated
            })));

        // The body is the whole file; `new` says nothing was at the path when the command made it.
        bridge.MapPost("/write", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct, bool @new = false) =>
            WithCall(http, vfs, async call =>
            {
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body, ct);
                return Json(await call.WriteAsync(path, body.ToArray(), @new, ct), _ => new { });
            }));

        bridge.MapPost("/delete", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct, bool directory = false) =>
            WithCall(http, vfs, async call => Json(await call.DeleteAsync(path, directory, ct), _ => new { })));

        // `overwrite` says something is already at `to`, which the bridge judges as a write there.
        bridge.MapPost("/rename", (HttpContext http, IVfsBridge vfs, string path, string to, CancellationToken ct, bool overwrite = false) =>
            WithCall(http, vfs, async call => Json(await call.RenameAsync(path, to, overwrite, ct), _ => new { })));

        // The launcher is about to kill the command: whatever its kill flushes arrives revoked and is
        // dropped. The token keeps answering, so the drops are recorded, until exec returns.
        bridge.MapPost("/revoke", (HttpContext http, IVfsBridge vfs) =>
            WithCall(http, vfs, call =>
            {
                vfs.Revoke(call.Token);
                return Task.FromResult(Results.Json(new { }));
            }));

        // An action file run from a script; the body carries the script's arguments.
        bridge.MapPost("/action", (HttpContext http, IVfsBridge vfs, string path, ActionRequest request, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.ActionAsync(path, request.Argv ?? [], ct), a => new
            {
                stdout = a.Stdout,
                stderr = a.Stderr,
                exitCode = a.ExitCode
            })));

        bridge.MapPost("/read", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => await call.ReadAsync(path, ct) switch
            {
                BridgeAnswer<byte[]>.Ok ok => Results.Bytes(ok.Value, "application/octet-stream"),
                var refused => Refused(refused)
            }));
    }

    public sealed record ActionRequest(IReadOnlyList<string>? Argv);

    private static async Task<IResult> WithCall(HttpContext http, IVfsBridge vfs, Func<VfsCall, Task<IResult>> answer)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith(SharedSecret.Scheme, StringComparison.Ordinal)
               && vfs.Find(header[SharedSecret.Scheme.Length..]) is { } call
            ? await answer(call)
            : Results.Unauthorized();
    }

    private static IResult Json<T>(BridgeAnswer<T> answer, Func<T, object> shape) => answer switch
    {
        BridgeAnswer<T>.Ok ok => Results.Json(shape(ok.Value)),
        _ => Refused(answer)
    };

    private static IResult Refused<T>(BridgeAnswer<T> answer) => answer is BridgeAnswer<T>.Refused refused
        ? Results.Json(new { errno = refused.Errno, error = refused.Error?.ToNode() }, statusCode: StatusCodes.Status422UnprocessableEntity)
        : throw new InvalidOperationException("Only a refusal is answered as one.");
}