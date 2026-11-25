using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Metrics;
using Infrastructure.Metrics;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;

namespace McpServerApps.Serving;

// A runtime error reported by a page of an app its viewer may open (AppPages settled who and which app):
// the page's word for what went wrong, held to AppErrorReport's shape; the server's for everything else.
//
// - The author's own report keeps the page's text. Another member's keeps only what the server checks
//   against what the origin serves (AppErrorReduction), since its page could write what it read about
//   that member into any word of the report and the author reads this file.
// - A person's report is also a household error the operator sees (an ErrorEvent under the app's own
//   household, on the deployment's metrics feed): its app's label and kind, and the author's own message
//   or a member's error type, never a stack nor a member's text.
// - A check's page reports too, and is answered as kept, but nothing is: its report.json already shows the
//   agent those page errors, every viewport of every check is a new page whose reports would push people's
//   out of the newest Errors:Kept, and no person met them.
// - At most Errors:PerSessionPerMinute reports a session a minute: one broken loop is one session, and
//   past it a report is answered 429 and kept nowhere until the minute is out. The client library holds
//   itself well under that.
public sealed class AppErrorReports(AppErrorLog log, AppsSettings settings, TimeProvider clock, IMetricsPublisher? metrics = null)
{
    public const string ServiceName = "mcp-apps";

    private readonly IMetricsPublisher _metrics = metrics ?? NoOpMetricsPublisher.Instance;

    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxUserAgent = 500;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _minutes = new(StringComparer.Ordinal);

    internal async Task ReportAsync(HttpContext context, BuiltApp app, AppSession session, AppPages.ServedFiles served, CancellationToken ct)
    {
        var request = context.Request;
        if (session.Proof is ViewerProof.CheckCall)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

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
        var label = $"{app.Slug}-{app.Id}{(session.Origin.Preview ? AppOrigin.PreviewSuffix : "")}";
        var entry = new AppErrorEntry
        {
            Time = clock.GetUtcNow(),
            Kind = report.Kind,
            Message = report.Message,
            Stack = report.Stack,
            Route = report.Route,
            UserAgent = userAgent.Length > MaxUserAgent ? userAgent[..MaxUserAgent] : userAgent,
            Viewer = session.Person.UserId,
            Origin = session.Origin.Preview ? AppErrorEntry.Preview : AppErrorEntry.Published,
            Read = report.Read
        };
        if (session.Person != app.Author)
        {
            entry = await ReducedAsync(entry, report, request.Host.Value ?? "", served, ct);
        }

        await log.RecordAsync(app, entry, ct);
        _metrics.ForHousehold(app.HouseholdId).Publish(new ErrorEvent
        {
            Service = ServiceName,
            ErrorType = AppErrors.ErrorType,
            Message = $"{label}: {report.Kind}: {entry.Message ?? entry.Name}"
        });

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // Another member's report, kept as no more than the server checks against what the origin serves
    // (AppErrorReduction): their page's text never reaches the author, who reads this file.
    private static async Task<AppErrorEntry> ReducedAsync(
        AppErrorEntry entry, AppErrorReport report, string authority, AppPages.ServedFiles served, CancellationToken ct)
    {
        // The first places on the origin, each path looked up once (a stack is the page's to make long) and
        // kept in the origin's own spelling.
        var places = AppErrorReduction.Places(report.Stack, authority).Take(AppErrorReduction.MaxFrames).ToList();
        var files = (await Task.WhenAll(places.Select(p => p.Path).Distinct(StringComparer.Ordinal)
                .Select(async path => (Path: path, Served: await served.ServedPathAsync(path, directories: false, ct)))))
            .Where(p => p.Served is not null).ToDictionary(p => p.Path, p => p.Served!, StringComparer.Ordinal);
        var frames = places.Where(p => files.ContainsKey(p.Path)).Select(p => (p with { Path = files[p.Path] }).ToString()).ToList();

        return entry with
        {
            Name = AppErrorReduction.NameOf(report.Message),
            Message = null,
            Stack = null,
            Frames = frames,
            Route = await served.ServedPathAsync(report.Route, directories: true, ct),
            UserAgent = null,
            Read = report.Read is { } read
                ? new AppErrorRead
                {
                    Operation = read.Operation,
                    Path = AppErrorReduction.MountOf(read.Path, served.Manifest.Reads),
                    Envelope = new AppErrorEnvelope { ErrorCode = read.Envelope.ErrorCode }
                }
                : null,
            Reduced = AppErrorReduction.Note
        };
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