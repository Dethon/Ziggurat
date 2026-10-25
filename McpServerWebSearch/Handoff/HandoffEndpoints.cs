using System.Net.WebSockets;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerWebSearch.Handoff;

// The person's side of a login handoff, under the assistants host's /browse path (Caddy routes it
// here; the host is VPN-only like WebChat). The link's GET only shows the page, because link
// previewers and prefetch issue GETs; the page's own claim spends the link and binds the view to a
// cookie only that browser holds. Everything after — frames, input, Done — answers to the cookie.
public static class HandoffEndpoints
{
    public const string ViewCookie = "zg_handoff";

    public static IEndpointRouteBuilder MapHandoff(this IEndpointRouteBuilder app)
    {
        app.MapGet("/browse/handoff/{token}", async (string token, LoginHandoffs handoffs) =>
            await handoffs.IsLinkLiveAsync(token)
                ? Results.Content(HandoffPage.Viewer(token), "text/html")
                : Gone());

        app.MapPost("/browse/handoff/{token}/claim", async (string token, HttpContext http, LoginHandoffs handoffs) =>
        {
            if (await handoffs.ClaimAsync(token) is not var (handoff, viewKey))
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            http.Response.Cookies.Append(ViewCookie, viewKey, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/browse",
                // Behind Caddy the request arrives as plain http; the person's browser spoke https.
                Secure = http.Request.IsHttps
                         || http.Request.Headers["X-Forwarded-Proto"] == "https"
            });
            return Results.Json(new { site = new Uri(handoff.Url).Host });
        });

        app.MapPost("/browse/handoff/done", async (HttpContext http, LoginHandoffs handoffs) =>
        {
            if (handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is not { } handoff)
            {
                return Results.Unauthorized();
            }

            return await handoffs.CompleteAsync(handoff)
                ? Results.NoContent()
                : Results.StatusCode(StatusCodes.Status410Gone);
        });

        app.Map("/browse/handoff/live", async (HttpContext http, LoginHandoffs handoffs, IHandoffBrowser browser) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is not { } handoff)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await RunViewAsync(socket, handoff, handoffs, browser, http.RequestAborted);
        });

        return app;
    }

    private static IResult Gone() =>
        Results.Content(HandoffPage.Gone, "text/html", statusCode: StatusCodes.Status410Gone);

    // Frames go out as binary messages, the newest only: a frame arriving while the last is still
    // being sent is dropped, so a slow phone sees the page late rather than ever later. Input comes
    // back as one JSON object per message.
    private static async Task RunViewAsync(
        WebSocket socket, LoginHandoffs.LiveHandoff handoff, LoginHandoffs handoffs,
        IHandoffBrowser browser, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var sending = 0;
        await using var stream = await browser.StreamHandoffAsync(handoff.Id, async frame =>
        {
            if (Interlocked.CompareExchange(ref sending, 1, 0) != 0 || socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await socket.SendAsync(frame.Jpeg, WebSocketMessageType.Binary, true, cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The view went away; the receive loop ends the session.
            }
            finally
            {
                Volatile.Write(ref sending, 0);
            }
        }, cts.Token);

        var watch = WatchEndAsync(socket, handoff, handoffs, cts.Token);
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, cts.Token);
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage && message.Length < 64 * 1024);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (HandoffInputMessage.Parse(message.ToArray()) is { } input)
                {
                    await browser.SendHandoffInputAsync(handoff.Id, input, cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The person closed the page, or the handoff ended underneath it.
        }
        finally
        {
            await cts.CancelAsync();
            await watch;
        }
    }

    // A handoff that ends while its view is open — Done from another request, its time running
    // out, the browser restarting — closes the view with a reason the page can show.
    private static async Task WatchEndAsync(
        WebSocket socket, LoginHandoffs.LiveHandoff handoff, LoginHandoffs handoffs, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!handoffs.IsOpen(handoff))
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, "ended", CancellationToken.None);
                    }

                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The view ended first.
        }
    }
}

// The view's input messages: {"type":"mousedown","x":0.4,"y":0.2,"button":"left"}, "mouseup",
// "mousemove", {"type":"wheel","dx":0,"dy":120}, {"type":"keydown","key":"Enter"}, "keyup",
// {"type":"text","value":"abc"}. Anything else is ignored.
public static class HandoffInputMessage
{
    public static HandoffInput? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            double Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return Text("type") switch
            {
                "mousemove" => new HandoffInput.MouseMove(Number("x"), Number("y")),
                "mousedown" => new HandoffInput.MouseDown(Number("x"), Number("y"), Text("button") ?? "left"),
                "mouseup" => new HandoffInput.MouseUp(Number("x"), Number("y"), Text("button") ?? "left"),
                "wheel" => new HandoffInput.Wheel(Number("dx"), Number("dy")),
                "keydown" when Text("key") is { Length: > 0 } key => new HandoffInput.KeyDown(key),
                "keyup" when Text("key") is { Length: > 0 } key => new HandoffInput.KeyUp(key),
                "text" when Text("value") is { } value => new HandoffInput.Text(value),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}