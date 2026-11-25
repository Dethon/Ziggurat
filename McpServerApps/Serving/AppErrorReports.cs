using Domain.Apps;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;

namespace McpServerApps.Serving;

// A runtime error reported by a page of an app its viewer may open (AppPages settled who and which app):
// the page's word for what went wrong, held to AppErrorReport's shape; the server's for everything else.
//
// - Only a page of this very origin reports here. Every app shares the apps domain, so a sibling app's
//   page is same-site and this origin's cookie rides its POST; its Origin header still names it.
// - At most Errors:PerSessionPerMinute reports a session a minute: one broken loop is one session, and
//   past it a report is answered 429 and kept nowhere until the minute is out. The client library holds
//   itself well under that.
public sealed class AppErrorReports(AppErrorLog log, AppsSettings settings, TimeProvider clock)
{
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxUserAgent = 500;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _minutes = new(StringComparer.Ordinal);

    // False for a request no page of this origin sent; the caller answers it the one 404.
    public static bool FromThisOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);

    public async Task ReportAsync(HttpContext context, BuiltApp app, AppSession session, CancellationToken ct)
    {
        var request = context.Request;
        if (!Admitted(request.Cookies[AppSessions.CookieName] ?? ""))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        if (await AppPages.BodyAsync(request, MaxBodyBytes, ct) is not { } body
            || AppErrorReport.Parse(body) is not AppErrorReportParse.Valid { Report: var report })
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        await log.RecordAsync(app, new AppErrorEntry
        {
            Time = clock.GetUtcNow(),
            Kind = report.Kind,
            Message = report.Message,
            Stack = report.Stack,
            Route = report.Route,
            UserAgent = userAgent.Length > MaxUserAgent ? userAgent[..MaxUserAgent] : userAgent,
            Viewer = session.Person.UserId,
            Origin = session.Origin.Preview ? "preview" : "published",
            Read = report.Read
        }, ct);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // A fixed minute per session, counted from its first report; minutes run out are let go as others arrive.
    private bool Admitted(string session)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var ended in _minutes.Where(m => now - m.Value.Start >= TimeSpan.FromMinutes(1)).Select(m => m.Key).ToList())
            {
                _minutes.Remove(ended);
            }

            var (start, count) = _minutes.GetValueOrDefault(session, (now, 0));
            if (count >= settings.Errors.PerSessionPerMinute)
            {
                return false;
            }

            _minutes[session] = (start, count + 1);
            return true;
        }
    }
}