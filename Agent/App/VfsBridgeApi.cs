using Domain.Contracts;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.AspNetCore.Http.Features;

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
            WithCall(http, vfs, async call => Json(
                await BodyWithinAsync(http, call.MaxFileBytes, ct) is { } content
                    ? await call.WriteAsync(path, content, @new, ct)
                    : call.WriteTooLarge(path, @new),
                _ => new { })));

        bridge.MapPost("/delete", (HttpContext http, IVfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.DeleteAsync(path, ct), _ => new { })));

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

    // The request's body, or null where it is longer than `ceiling`: refused on the length it
    // declares where it declares one, and otherwise read no further than one byte past, so a body
    // this is about to refuse is never held. The server's own request limit is moved to match —
    // its default is below the ceiling, and would answer a file that fits with a 413 instead.
    private static async Task<byte[]?> BodyWithinAsync(HttpContext http, long ceiling, CancellationToken ct)
    {
        if (http.Request.ContentLength > ceiling)
        {
            return null;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ceiling + 1;
        }

        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > ceiling)
            {
                return null;
            }

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    private static async Task<IResult> WithCall(HttpContext http, IVfsBridge vfs, Func<VfsCall, Task<IResult>> answer) =>
        SharedSecret.Bearer(http.Request.Headers.Authorization.ToString()) is { } token && vfs.Find(token) is { } call
            ? await answer(call)
            : Results.Unauthorized();

    private static IResult Json<T>(BridgeAnswer<T> answer, Func<T, object> shape) => answer switch
    {
        BridgeAnswer<T>.Ok ok => Results.Json(shape(ok.Value)),
        _ => Refused(answer)
    };

    private static IResult Refused<T>(BridgeAnswer<T> answer) => answer is BridgeAnswer<T>.Refused refused
        ? Results.Json(new { errno = refused.Errno, error = refused.Error?.ToNode() }, statusCode: StatusCodes.Status422UnprocessableEntity)
        : throw new InvalidOperationException("Only a refusal is answered as one.");
}