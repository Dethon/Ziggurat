using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using McpServerSugary.Glucose;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tests.Integration.Fixtures;

// LibreLinkUp, faked on its wire, so no test reaches Abbott. Every region is served at once under a
// path prefix of its own (`/eu/llu/...`, `/global/llu/...`), which is what Regions points a client at:
// a login at a region the account does not live in answers the redirect the real service answers, and
// a token is good only at the region that issued it. Requests are refused as the real service refuses
// them — a login without the app's product and version is told the version is too old, a call without
// the token or the Account-Id its login implies is a 401 — so a test that passes proves the headers.
// Every answer is gzipped when the request accepts it, as Abbott's are. The clock is the test's, so a
// token's `expires` and a reading's age follow a FakeTimeProvider.
public sealed class FakeLibreLinkUp : IAsyncDisposable
{
    public const string AppProduct = "llu.android";
    public const string AppVersion = "4.16.0";

    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Issued> _tokens = new();
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<string> _unhandled = new();
    private readonly ConcurrentQueue<Func<HttpContext, Task<bool>>> _interruptions = new();
    private readonly TimeProvider _clock;
    private WebApplication _app = null!;
    private int _port;

    private FakeLibreLinkUp(TimeProvider clock) => _clock = clock;

    public string Root => $"http://127.0.0.1:{_port}/";

    // Where a client finds each region on this fake.
    public LibreLinkUpRegions Regions => new($"{Root}global/", $"{Root}{{region}}/");

    // How long a token a login issues is good for; the real service's is about 180 days.
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(180);

    // The version a login must claim at least; a lower one is answered status 920.
    public string MinimumVersion { get; set; } = AppVersion;

    // Changes the authTicket a login answers before it is sent, as a service might word it otherwise:
    // its expiry in milliseconds, or none at all. The token itself still lasts TokenLifetime.
    public Action<JsonObject>? AlterTicket { get; set; }

    public IReadOnlyList<Request> Requests => [.. _requests];

    public IReadOnlyList<Request> Logins => [.. _requests.Where(r => r.Path.EndsWith("/llu/auth/login"))];

    public IReadOnlyList<string> Unhandled => [.. _unhandled];

    public static async Task<FakeLibreLinkUp> StartAsync(TimeProvider? clock = null)
    {
        var fake = new FakeLibreLinkUp(clock ?? TimeProvider.System) { _port = TestPort.GetAvailable() };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, fake._port));
        fake._app = builder.Build();

        fake._app.Use(async (context, next) =>
        {
            fake._requests.Enqueue(await Request.OfAsync(context));
            if (fake._interruptions.TryDequeue(out var interrupt) && await interrupt(context))
            {
                return;
            }

            await next(context);
        });
        fake._app.MapPost("/{region}/llu/auth/login", (HttpContext http, string region) => fake.LogInAsync(http, region));
        fake._app.MapGet("/{region}/llu/connections", (HttpContext http, string region) => fake.ConnectionsAsync(http, region));
        fake._app.MapGet("/{region}/llu/connections/{patientId}/graph",
            (HttpContext http, string region, string patientId) => fake.GraphAsync(http, region, patientId));
        fake._app.MapFallback((HttpContext http) =>
        {
            fake._unhandled.Enqueue($"{http.Request.Method} {http.Request.Path}");
            return Results.NotFound();
        });

        await fake._app.StartAsync();
        return fake;
    }

    // A follower account, living at a region ("eu", "us", ...).
    public Account AddAccount(string email, string password, string region = "eu", string? userId = null)
    {
        var account = new Account(email, password, region, userId ?? Guid.NewGuid().ToString());
        _accounts[email] = account;
        return account;
    }

    // Every authenticated call is a 401 however fresh its token, as the service answers an account it
    // has locked out of the follower API while still letting it log in.
    public bool RefuseEveryToken { get; set; }

    // Every token issued so far stops working, as an expired or revoked one does: the next call is a 401.
    public void RevokeTokens() => _tokens.Clear();

    // The next request, whatever it asks, is answered with this status, this body and these headers.
    public void InterruptNext(HttpStatusCode status, string? body = null, params (string Name, string Value)[] headers) =>
        _interruptions.Enqueue(async context =>
        {
            context.Response.StatusCode = (int)status;
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }

            if (body is not null)
            {
                await WriteAsync(context, body);
            }

            return true;
        });

    // The next request is answered 200 with this envelope, as a body status the service sends.
    public void AnswerNext(string body) => _interruptions.Enqueue(async context =>
    {
        await WriteAsync(context, body);
        return true;
    });

    // The SHA-256 of a user id, lowercase hex: the Account-Id the app sends since 4.16.0.
    public static string AccountIdOf(string userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));

    // The service's own timestamp wording, in invariant culture: "5/21/2022 1:38:50 PM".
    public static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

    private async Task LogInAsync(HttpContext http, string region)
    {
        var body = JsonNode.Parse(await new StreamReader(http.Request.Body).ReadToEndAsync())?.AsObject();
        if (http.Request.Headers["product"] != AppProduct
            || !Version.TryParse(http.Request.Headers["version"], out var version)
            || version < Version.Parse(MinimumVersion))
        {
            await WriteAsync(http, new JsonObject
            {
                ["status"] = 920,
                ["data"] = new JsonObject { ["minimumVersion"] = MinimumVersion }
            });
            return;
        }

        if (body?["email"]?.GetValue<string>() is not { } email
            || !_accounts.TryGetValue(email, out var account)
            || body["password"]?.GetValue<string>() != account.Password)
        {
            await WriteAsync(http, new JsonObject
            {
                ["status"] = 2,
                ["error"] = new JsonObject { ["message"] = "notAuthenticated" }
            });
            return;
        }

        if (account.Step is { } step)
        {
            await WriteAsync(http, new JsonObject
            {
                ["status"] = 4,
                ["data"] = new JsonObject
                {
                    ["step"] = new JsonObject { ["type"] = "tou", ["componentName"] = step, ["props"] = new JsonObject() }
                }
            });
            return;
        }

        if (!string.Equals(region, account.Region, StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(http, new JsonObject
            {
                ["status"] = 0,
                ["data"] = new JsonObject { ["redirect"] = true, ["region"] = account.Region }
            });
            return;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = _clock.GetUtcNow() + TokenLifetime;
        _tokens[token] = new Issued(account, region, expires);
        var ticket = new JsonObject
        {
            ["token"] = token,
            ["expires"] = expires.ToUnixTimeSeconds(),
            ["duration"] = (long)TokenLifetime.TotalMilliseconds
        };
        AlterTicket?.Invoke(ticket);
        await WriteAsync(http, new JsonObject
        {
            ["status"] = 0,
            ["data"] = new JsonObject
            {
                ["user"] = new JsonObject { ["id"] = account.UserId, ["email"] = account.Email, ["country"] = "ES" },
                ["authTicket"] = ticket
            }
        });
    }

    private async Task ConnectionsAsync(HttpContext http, string region)
    {
        if (Authenticated(http, region) is not { } account)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await WriteAsync(http, new JsonObject
        {
            ["status"] = 0,
            ["data"] = new JsonArray([.. account.Connections.Values.Select(c => (JsonNode)c.ToJson())])
        });
    }

    private async Task GraphAsync(HttpContext http, string region, string patientId)
    {
        if (Authenticated(http, region) is not { } account)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!account.Connections.TryGetValue(patientId, out var connection))
        {
            await WriteAsync(http, new JsonObject { ["status"] = 0, ["data"] = null });
            return;
        }

        await WriteAsync(http, new JsonObject
        {
            ["status"] = 0,
            ["data"] = new JsonObject
            {
                ["connection"] = connection.ToJson(),
                ["activeSensors"] = new JsonArray(
                [
                    .. connection.Sensors.Select(s => (JsonNode)new JsonObject
                    {
                        ["sensor"] = s.ToJson(),
                        ["device"] = new JsonObject { ["did"] = "device", ["dtid"] = 40068, ["v"] = "3.3.1" }
                    })
                ]),
                ["graphData"] = new JsonArray([.. connection.Graph.Select(p => (JsonNode)Measurement(p, null))])
            }
        });
    }

    private Account? Authenticated(HttpContext http, string region)
    {
        var bearer = http.Request.Headers.Authorization.ToString();
        if (RefuseEveryToken
            || !bearer.StartsWith("Bearer ", StringComparison.Ordinal)
            || !_tokens.TryGetValue(bearer["Bearer ".Length..], out var issued)
            || !string.Equals(issued.Region, region, StringComparison.OrdinalIgnoreCase)
            || issued.Expires <= _clock.GetUtcNow()
            || http.Request.Headers["product"] != AppProduct
            || http.Request.Headers["version"] != AppVersion
            || http.Request.Headers["Account-Id"] != AccountIdOf(issued.Account.UserId))
        {
            return null;
        }

        return issued.Account;
    }

    private static JsonObject Measurement(Point point, int? trendArrow)
    {
        var item = new JsonObject
        {
            ["FactoryTimestamp"] = Stamp(point.At),
            // The phone's own clock, with no offset: two hours off UTC here, which a client must ignore.
            ["Timestamp"] = Stamp(point.At + TimeSpan.FromHours(2)),
            ["type"] = trendArrow is null ? 0 : 1,
            ["ValueInMgPerDl"] = point.MgPerDl,
            ["MeasurementColor"] = 1,
            ["GlucoseUnits"] = 1,
            ["Value"] = point.MgPerDl,
            ["isHigh"] = point.MgPerDl > 250,
            ["isLow"] = point.MgPerDl < 70
        };
        if (trendArrow is { } arrow)
        {
            item["TrendArrow"] = arrow;
            item["TrendMessage"] = null;
        }

        return item;
    }

    private static Task WriteAsync(HttpContext http, JsonNode body) => WriteAsync(http, body.ToJsonString());

    private static async Task WriteAsync(HttpContext http, string body)
    {
        http.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        if (!http.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            await http.Response.Body.WriteAsync(bytes);
            return;
        }

        http.Response.Headers.ContentEncoding = "gzip";
        await using var zipped = new GZipStream(http.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
        await zipped.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        TestPort.Release(_port);
    }

    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

        // The region prefix the request was sent to: "eu", "global", ...
        public string Region => Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        public static async Task<Request> OfAsync(HttpContext context)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            return new Request(
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
                body);
        }
    }

    public sealed record Point(DateTimeOffset At, double MgPerDl);

    public sealed record Sensor(string Serial, DateTimeOffset ActivatedAt, int ProductType = 4)
    {
        public JsonObject ToJson() => new()
        {
            ["deviceId"] = "",
            ["sn"] = Serial,
            ["a"] = ActivatedAt.ToUnixTimeSeconds(),
            ["w"] = 60,
            ["pt"] = ProductType
        };
    }

    public sealed class Account(string email, string password, string region, string userId)
    {
        public string Email { get; } = email;
        public string Password { get; } = password;
        public string Region { get; } = region;
        public string UserId { get; } = userId;

        // A step the app must be opened to accept (terms of use), answered as status 4 on login.
        public string? Step { get; set; }

        internal ConcurrentDictionary<string, Connection> Connections { get; } = new();

        public Connection Follow(string patientId, string firstName, string lastName)
        {
            var connection = new Connection(patientId, firstName, lastName);
            Connections[patientId] = connection;
            return connection;
        }
    }

    public sealed class Connection(string patientId, string firstName, string lastName)
    {
        public string PatientId { get; } = patientId;
        public string FirstName { get; } = firstName;
        public string LastName { get; } = lastName;
        public double TargetLow { get; set; } = 70;
        public double TargetHigh { get; set; } = 180;

        // The sensor on her now; null for a connection whose sensor has ended and none replaced it.
        public Sensor? Current { get; set; }

        // Every sensor the graph lists as active, the current one first.
        public IReadOnlyList<Sensor> Sensors { get; set; } = [];

        // The newest reading with its arrow; null before a sensor ever sent one.
        public (Point Point, int TrendArrow)? Latest { get; set; }

        // About twelve hours of five-minute points.
        public IReadOnlyList<Point> Graph { get; set; } = [];

        public JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["id"] = $"connection-{PatientId}",
                ["patientId"] = PatientId,
                ["country"] = "ES",
                ["status"] = 2,
                ["firstName"] = FirstName,
                ["lastName"] = LastName,
                ["targetLow"] = TargetLow,
                ["targetHigh"] = TargetHigh,
                ["uom"] = 1,
                ["sensor"] = Current?.ToJson(),
                ["glucoseMeasurement"] = Latest is { } latest ? Measurement(latest.Point, latest.TrendArrow) : null
            };
            json["glucoseItem"] = json["glucoseMeasurement"]?.DeepClone();
            return json;
        }
    }

    private sealed record Issued(Account Account, string Region, DateTimeOffset Expires);
}