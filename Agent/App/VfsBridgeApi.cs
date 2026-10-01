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

        bridge.MapPost("/attr", (HttpContext http, VfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.AttrAsync(path, ct), a => new { kind = a.Kind, size = a.Size })));

        bridge.MapPost("/list", (HttpContext http, VfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => Json(await call.ListAsync(path, ct), l => new
            {
                entries = l.Entries.Select(e => new { name = e.Name, kind = e.Kind }),
                truncated = l.Truncated
            })));

        bridge.MapPost("/read", (HttpContext http, VfsBridge vfs, string path, CancellationToken ct) =>
            WithCall(http, vfs, async call => await call.ReadAsync(path, ct) switch
            {
                BridgeAnswer<byte[]>.Ok ok => Results.Bytes(ok.Value, "application/octet-stream"),
                var refused => Refused(refused)
            }));
    }

    private static async Task<IResult> WithCall(HttpContext http, VfsBridge vfs, Func<VfsCall, Task<IResult>> answer)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.Ordinal) && vfs.Find(header[scheme.Length..]) is { } call
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