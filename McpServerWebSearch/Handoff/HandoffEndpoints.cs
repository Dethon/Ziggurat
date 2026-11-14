using System.Net.WebSockets;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Infrastructure.Clients.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerWebSearch.Handoff;

// The person's side of a login handoff, under the main host's /browse/<household> path (the relay
// forwards it down this server's tunnel to its pages listener, which answers nothing else —
// PagesListener). The household is the one the link was minted for (BrowsePagePath), and this server
// is that household's, so it is read off the path only to scope the cookie and the page's own
// requests to it. The link's GET only shows the page, because link previewers and prefetch issue
// GETs; the page's own claim spends the link and binds the view to a cookie only that browser holds.
// Everything after — frames, input, Done — answers to the cookie.
public static class HandoffEndpoints
{
    public const string ViewCookie = BrowsePagePolicy.ViewCookie;
    public const string LoginsCookie = BrowsePagePolicy.LoginsCookie;

    // Sent back to this household's pages alone: one browser holding views of two households' pages
    // must never hand one household's server the other's key.
    private static CookieOptions ViewCookieOptions(HttpContext http, string household) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = BrowsePagePath.For(household),
        // Behind Caddy the request arrives as plain http; the person's browser spoke https.
        Secure = http.Request.IsHttps || http.Request.Headers["X-Forwarded-Proto"] == "https"
    };

    public static IEndpointRouteBuilder MapHandoff(this IEndpointRouteBuilder endpoints)
    {
        var app = endpoints.MapGroup($"{BrowsePagePath.Root}/{{household}}");

        // The link's page, and the same page again for the browser that claimed it — reloaded, or brought
        // back after the phone let it go — while its login is open; a spent link to anyone else.
        app.MapGet("/handoff/{token}", async (string household, string token, HttpContext http, LoginHandoffs handoffs) =>
            await handoffs.IsLinkLiveAsync(token)
            || (handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is { } claimed && claimed.Token == token && handoffs.IsOpen(claimed))
                ? Results.Content(HandoffPage.Viewer(BrowsePagePath.For(household), token, handoffs.LinkMinutes), "text/html")
                : Gone(handoffs.LinkMinutes));

        app.MapPost("/handoff/{token}/claim", async (string household, string token, HttpContext http, LoginHandoffs handoffs) =>
        {
            if (await handoffs.ClaimAsync(token) is not var (handoff, viewKey))
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            http.Response.Cookies.Append(ViewCookie, viewKey, ViewCookieOptions(http, household));
            return Results.Json(new
            {
                site = new Uri(handoff.Url).Host,
                canRemember = handoffs.CanRemember,
                from = AskedFrom(handoff.Context)
            });
        });

        // Whether the claimed login is still open: a phone that left for its authenticator app comes
        // back to a dropped socket, and connects again rather than calling the login ended; a page
        // opened again takes up the login its browser claimed, its link being spent.
        app.MapGet("/handoff/state", (HttpContext http, LoginHandoffs handoffs) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is { } handoff
                ? Results.Json(new
                {
                    open = handoffs.IsOpen(handoff),
                    site = new Uri(handoff.Url).Host,
                    canRemember = handoffs.CanRemember,
                    from = AskedFrom(handoff.Context)
                })
                : Results.Unauthorized();
        });

        app.MapGet("/logins/{token}", async (string household, string token, LoginsPages pages) =>
            await pages.IsLinkLiveAsync(token)
                ? Results.Content(HandoffPage.Logins(BrowsePagePath.For(household), token, pages.LinkMinutes), "text/html")
                : Gone(pages.LinkMinutes));

        app.MapPost("/logins/{token}/claim", async (string household, string token, HttpContext http, LoginsPages pages) =>
        {
            if (await pages.ClaimAsync(token) is not { } viewKey)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            http.Response.Cookies.Append(LoginsCookie, viewKey, ViewCookieOptions(http, household));
            return Results.NoContent();
        });

        // One entry per login, by the site it was handed off for, with every domain it covers.
        app.MapGet("/logins/sites", async (HttpContext http, LoginsPages pages, PersonLogins logins) =>
            pages.PersonOf(http.Request.Cookies[LoginsCookie]) is { } person
                ? Results.Json((await logins.LoginsAsync(person)).Select(login => new
                {
                    site = login.Site,
                    remembered = login.Remembered,
                    handedOffAt = login.HandedOffAt,
                    lastUsedAt = login.LastUsedAt,
                    domains = login.Domains
                }))
                : Results.Unauthorized());

        app.MapPost("/logins/forget", async (HttpContext http, LoginsPages pages, PersonLogins logins) =>
        {
            if (pages.PersonOf(http.Request.Cookies[LoginsCookie]) is not { } person)
            {
                return Results.Unauthorized();
            }

            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            if (!body.RootElement.TryGetProperty("site", out var site) || site.GetString() is not { Length: > 0 } login)
            {
                return Results.BadRequest();
            }

            return await logins.ForgetAsync(person, login)
                ? Results.NoContent()
                : Results.Conflict();
        });

        app.MapPost("/handoff/done", async (HttpContext http, LoginHandoffs handoffs) =>
        {
            if (handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is not { } handoff)
            {
                return Results.Unauthorized();
            }

            return await handoffs.CompleteAsync(handoff, await RememberAsync(http))
                ? Results.NoContent()
                : Results.StatusCode(StatusCodes.Status410Gone);
        });

        app.Map("/handoff/live", async (
            HttpContext http, LoginHandoffs handoffs, IHandoffBrowser browser, TimeProvider time) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // A socket opened by another site's page: SameSite already keeps the cookie off it, and
            // this says so without leaning on the browser.
            if (http.Request.Headers.Origin is [{ } origin]
                && !(Uri.TryCreate(origin, UriKind.Absolute, out var from)
                     && string.Equals(from.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (handoffs.ByViewKey(http.Request.Cookies[ViewCookie]) is not { } handoff)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await RunViewAsync(socket, handoff, handoffs, browser, time, http.RequestAborted);
        });

        return endpoints;
    }

    // Ticked unless the page said otherwise: remembering is the default.
    private static async Task<bool> RememberAsync(HttpContext http)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            return !body.RootElement.TryGetProperty("remember", out var remember)
                   || remember.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // Where the person goes back to once the login is done, as the page words it: the chat (a voice
    // login's link is put there too), Telegram, or nowhere the page knows. The page navigates only to
    // the chat's own front, so a server saying something else here leads nowhere new.
    private static string AskedFrom(ConversationContext context) =>
        context.Origin.ChannelId switch
        {
            ChannelIds.SignalR or ChannelIds.Voice => "chat",
            ChannelIds.Telegram => "telegram",
            _ => "elsewhere"
        };

    private static IResult Gone(int minutes) =>
        Results.Content(HandoffPage.Gone(minutes), "text/html", statusCode: StatusCodes.Status410Gone);

    // Frames go out as binary messages, the newest only: a frame arriving while the last is still
    // being sent is dropped, so a slow phone sees the page late rather than ever later. Input comes
    // back as one JSON object per message. One send at a time — a WebSocket takes no concurrent
    // sends — so the close a finished handoff sends waits for the frame in flight.
    private static async Task RunViewAsync(
        WebSocket socket, LoginHandoffs.LiveHandoff handoff, LoginHandoffs handoffs,
        IHandoffBrowser browser, TimeProvider time, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        using var sending = new SemaphoreSlim(1, 1);
        await using var stream = await browser.StreamHandoffAsync(handoff.Id, async frame =>
        {
            if (!await sending.WaitAsync(0))
            {
                return;
            }

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(frame.Jpeg, WebSocketMessageType.Binary, true, cts.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The view went away; the receive loop ends the session.
            }
            finally
            {
                sending.Release();
            }
        }, async fields =>
        {
            // Rare and small, so never dropped: it waits for the frame in flight.
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "fields",
                fields = fields.Select(f => new
                {
                    x = f.X,
                    y = f.Y,
                    width = f.Width,
                    height = f.Height,
                    kind = f.Kind,
                    focused = f.Focused
                })
            });
            try
            {
                await sending.WaitAsync(cts.Token);
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.SendAsync(json, WebSocketMessageType.Text, true, cts.Token);
                    }
                }
                finally
                {
                    sending.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The view went away; the receive loop ends the session.
            }
        }, cts.Token);

        var watch = WatchEndAsync(socket, handoff, handoffs, sending, time, cts.Token);
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
        WebSocket socket, LoginHandoffs.LiveHandoff handoff, LoginHandoffs handoffs, SemaphoreSlim sending,
        TimeProvider time, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (handoffs.IsOpen(handoff))
                {
                    continue;
                }

                await sending.WaitAsync(ct);
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ended", CancellationToken.None);
                    }
                }
                finally
                {
                    sending.Release();
                }

                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The view ended first.
        }
    }
}

// The view's input messages: {"type":"mousedown","x":0.4,"y":0.2,"button":"left"}, "mouseup",
// "mousemove", {"type":"wheel","dx":0,"dy":120}, {"type":"keydown","key":"Enter"}, "keyup",
// {"type":"text","value":"abc"}, {"type":"resize","width":390,"height":700}. Anything else is ignored.
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
                "resize" => new HandoffInput.Resize((int)Number("width"), (int)Number("height")),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}