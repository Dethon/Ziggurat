using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// What a LibreLinkUp call answered: the value, with the region the account was found at (null for
// the global address) so a link keeps it, or why there is none.
public abstract record LibreLinkUpAnswer<T>
{
    private LibreLinkUpAnswer()
    {
    }

    public sealed record Ok(T Value, string? Region) : LibreLinkUpAnswer<T>;

    public sealed record Failed(LibreLinkUpFailure Failure) : LibreLinkUpAnswer<T>;
}

// Why LibreLinkUp gave no answer. Only CredentialsRefused is fixed by typing the password again; a
// step and an old version are fixed elsewhere (the LibreLinkUp app, this server's release), and the
// other two pass.
public abstract record LibreLinkUpFailure
{
    private LibreLinkUpFailure()
    {
    }

    public sealed record CredentialsRefused : LibreLinkUpFailure;

    // Something to accept in the LibreLinkUp app first (new terms, a privacy notice), by the name the
    // service gives the screen.
    public sealed record StepRequired(string Component) : LibreLinkUpFailure;

    // The app version this client claims is no longer served. The operator's to fix.
    public sealed record VersionTooOld(string? MinimumVersion) : LibreLinkUpFailure;

    // Asked too often. RetryAfter is how long the service said to wait, null when it did not say.
    public sealed record RateLimited(TimeSpan? RetryAfter) : LibreLinkUpFailure;

    // No usable answer: nothing listening, a timeout, a server error, a body that is not the service's
    // envelope, or a token refused even fresh. Reason is for the log, never for her.
    public sealed record Unreachable(string Reason) : LibreLinkUpFailure;
}

// LibreLinkUp as its Android app speaks it (community-documented; Abbott publishes nothing). A token
// lasts about six months, so it is kept per account until its expiry and replaced only then or when a
// call is refused 401/403 — never per poll, which is what gets an account rate-limited. It must be one
// instance for the server's life (AddLibreLinkUp), or every resolution logs in again.
public sealed class LibreLinkUpClient(
    HttpClient http, LibreLinkUpRegions regions, TimeProvider clock, ILogger<LibreLinkUpClient> logger)
{
    public const string Product = "llu.android";
    public const string AppVersion = "4.16.0";

    // A token this close to its expiry is replaced before it is sent rather than after it is refused.
    private static readonly TimeSpan _renewalMargin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _logins = new();

    // The handler the service needs: it gzips every answer, and a singleton's connections must still
    // see DNS change.
    public static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public Task<LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>> ConnectionsAsync(
        LibreLinkUpAccount account, CancellationToken ct) =>
        CallAsync(account, "llu/connections", LibreLinkUpWire.Connections, ct);

    public Task<LibreLinkUpAnswer<LibreLinkUpGraph>> GraphAsync(
        LibreLinkUpAccount account, string patientId, CancellationToken ct) =>
        CallAsync(account, $"llu/connections/{Uri.EscapeDataString(patientId)}/graph", LibreLinkUpWire.Graph, ct);

    private async Task<LibreLinkUpAnswer<T>> CallAsync<T>(
        LibreLinkUpAccount account, string path, Func<JsonNode?, T?> parse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var key = KeyOf(account);

        Session? refused = null;
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var session = await SessionAsync(key, account, refused, ct);
            if (session is LibreLinkUpAnswer<Session>.Failed failed)
            {
                return new LibreLinkUpAnswer<T>.Failed(failed.Failure);
            }

            var live = ((LibreLinkUpAnswer<Session>.Ok)session).Value;
            switch (await SendAsync(() => Authenticated(live, path), ct))
            {
                case Reply.Unauthorized:
                    refused = live;
                    continue;
                case Reply.Failed reply:
                    return new LibreLinkUpAnswer<T>.Failed(reply.Failure);
                case Reply.Data data when parse(data.Value) is { } value:
                    return new LibreLinkUpAnswer<T>.Ok(value, live.Region);
                default:
                    return Unreachable<T>($"{path} answered no {typeof(T).Name}");
            }
        }

        return Unreachable<T>($"{path} refused a token issued a moment before");
    }

    // The account's kept token, or a new one when there is none, it is about to expire, or it is the
    // one a call was just refused with. One login per account at a time: polls and a link being saved
    // may ask at once, and each would otherwise log in.
    private async Task<LibreLinkUpAnswer<Session>> SessionAsync(
        string key, LibreLinkUpAccount account, Session? refused, CancellationToken ct)
    {
        var gate = _logins.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(key, out var kept)
                && kept != refused
                && kept.Expires - _renewalMargin > clock.GetUtcNow())
            {
                return new LibreLinkUpAnswer<Session>.Ok(kept, kept.Region);
            }

            _sessions.TryRemove(key, out _);
            var login = await LogInAsync(account, ct);
            if (login is LibreLinkUpAnswer<Session>.Ok ok)
            {
                _sessions[key] = ok.Value;
            }

            return login;
        }
        finally
        {
            gate.Release();
        }
    }

    // A login at the region the account was last found, or the global address; an account living
    // elsewhere is answered with its region, and logged in again there once.
    private async Task<LibreLinkUpAnswer<Session>> LogInAsync(LibreLinkUpAccount account, CancellationToken ct)
    {
        var region = LibreLinkUpRegions.Normalize(account.Region);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            if (regions.BaseFor(region) is not { } address)
            {
                return Unreachable<Session>($"no address for region '{region}'");
            }

            var reply = await SendAsync(() => Login(address, account), ct);
            switch (reply)
            {
                case Reply.Failed failed:
                    return new LibreLinkUpAnswer<Session>.Failed(failed.Failure);
                case Reply.Unauthorized:
                    return Unreachable<Session>("the login was answered 401/403");
            }

            var data = ((Reply.Data)reply).Value;
            if (data?["redirect"]?.GetValueKind() == JsonValueKind.True)
            {
                var to = LibreLinkUpWire.Text(data["region"]);
                if (LibreLinkUpRegions.Normalize(to) is not { } next || next == region)
                {
                    return Unreachable<Session>($"redirected to an unusable region '{to}'");
                }

                logger.LogInformation("LibreLinkUp moved a login from {From} to region {Region}", region ?? "global", next);
                region = next;
                continue;
            }

            if (LibreLinkUpWire.Text(data?["authTicket"]?["token"]) is not { } token
                || LibreLinkUpWire.Text(data?["user"]?["id"]) is not { } userId)
            {
                return Unreachable<Session>("the login answered no token");
            }

            var now = clock.GetUtcNow();
            var expires = LibreLinkUpWire.Number(data?["authTicket"]?["expires"]) is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
                : now + TimeSpan.FromMilliseconds(LibreLinkUpWire.Number(data?["authTicket"]?["duration"]) ?? 0);
            return new LibreLinkUpAnswer<Session>.Ok(
                new Session(address, region, token, AccountIdOf(userId), expires), region);
        }

        return Unreachable<Session>("redirected more than once");
    }

    private static HttpRequestMessage Login(Uri address, LibreLinkUpAccount account)
    {
        var request = Request(HttpMethod.Post, new Uri(address, "llu/auth/login"));
        request.Content = new StringContent(
            new JsonObject { ["email"] = account.Email, ["password"] = account.Password }.ToJsonString(),
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/json"));
        return request;
    }

    private static HttpRequestMessage Authenticated(Session session, string path)
    {
        var request = Request(HttpMethod.Get, new Uri(session.Address, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("Account-Id", session.AccountId);
        return request;
    }

    // The app's headers. Accept-Encoding is the handler's (CreateHttp), which also undoes it; the app's
    // content-type on a GET is not sent, since a request without a body has no content to carry it.
    private static HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("product", Product);
        request.Headers.Add("version", AppVersion);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Connection.Add("Keep-Alive");
        return request;
    }

    private async Task<Reply> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        try
        {
            using var request = build();
            using var response = await http.SendAsync(request, ct);
            switch ((int)response.StatusCode)
            {
                case 401 or 403:
                    return new Reply.Unauthorized();
                case 429 or 430:
                    return new Reply.Failed(new LibreLinkUpFailure.RateLimited(RetryAfter(response)));
                case < 200 or > 299:
                    return Fails($"HTTP {(int)response.StatusCode}");
            }

            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject envelope
                || LibreLinkUpWire.Number(envelope["status"]) is not { } status)
            {
                return Fails("an answer that is not LibreLinkUp's envelope");
            }

            var data = envelope["data"];
            switch (status)
            {
                case 0:
                    return new Reply.Data(data);
                case 2:
                    return new Reply.Failed(new LibreLinkUpFailure.CredentialsRefused());
                case 4:
                    return new Reply.Failed(new LibreLinkUpFailure.StepRequired(
                        LibreLinkUpWire.Text(data?["step"]?["componentName"]) ?? "unknown"));
                case 920:
                    var minimum = LibreLinkUpWire.Text(data?["minimumVersion"]);
                    logger.LogError(
                        "LibreLinkUp no longer serves app version {Version}; it asks for {Minimum}. Raise LibreLinkUpClient.AppVersion",
                        AppVersion, minimum ?? "a newer one");
                    return new Reply.Failed(new LibreLinkUpFailure.VersionTooOld(minimum));
                default:
                    return Fails($"status {status}");
            }
        }
        catch (HttpRequestException e)
        {
            return Fails(e.HttpRequestError.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fails("timed out");
        }
        catch (JsonException)
        {
            return Fails("an answer that is not JSON");
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - clock.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null
    };

    private static Reply.Failed Fails(string reason) => new(new LibreLinkUpFailure.Unreachable(reason));

    private static LibreLinkUpAnswer<T> Unreachable<T>(string reason) =>
        new LibreLinkUpAnswer<T>.Failed(new LibreLinkUpFailure.Unreachable(reason));

    // Since app 4.16.0 every call names the account by the SHA-256 of its user id.
    private static string AccountIdOf(string userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));

    // Keyed by the password too, so a wrong one is never answered with the right one's token; the key
    // is a hash, so the cache holds no password of its own.
    private static string KeyOf(LibreLinkUpAccount account) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{account.Email.Trim().ToLowerInvariant()}\n{account.Password}")));

    private sealed record Session(Uri Address, string? Region, string Token, string AccountId, DateTimeOffset Expires);

    private abstract record Reply
    {
        public sealed record Data(JsonNode? Value) : Reply;

        public sealed record Unauthorized : Reply;

        public sealed record Failed(LibreLinkUpFailure Failure) : Reply;
    }
}

public static class LibreLinkUpRegistration
{
    // One client for the server's life: the token it keeps is the point.
    public static IServiceCollection AddLibreLinkUp(this IServiceCollection services, LibreLinkUpRegions regions)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new LibreLinkUpClient(
            LibreLinkUpClient.CreateHttp(),
            regions,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LibreLinkUpClient>>()));
        return services;
    }
}