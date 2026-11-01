using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Accounts.Settings;
using Domain.DTOs.Household;

namespace Accounts.Keys;

// What a provider said of a key: it works, it refused it (a definite authentication failure, which
// refuses the save), or it could not be asked (down, slow, or answering something that says nothing
// about the key), which saves the key unchecked.
public abstract record KeyProbeOutcome
{
    public sealed record Working : KeyProbeOutcome;

    public sealed record Rejected(string Why) : KeyProbeOutcome;

    public sealed record Unreachable(string Why) : KeyProbeOutcome;
}

// Asks a key's provider whether it takes the key, without spending anything on it. One contract, one
// probe per provider: adding a provider is one more probe.
public interface IKeyProbe<in TKey>
{
    string Provider { get; }

    Task<KeyProbeOutcome> ProbeAsync(TKey key, CancellationToken ct);
}

public static partial class KeyProbes
{
    // Its own client, with the request loggers taken off: a Telegram bot's token is in the URL it
    // asks, and the default handlers log every request's URL.
    public const string ClientName = "key-probes";

    public static IServiceCollection AddKeyProbes(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName).RemoveAllLoggers();
        return services
            .AddSingleton<IKeyProbe<OpenRouterKey>, OpenRouterKeyProbe>()
            .AddSingleton<IKeyProbe<IdealistaKeys>, IdealistaKeyProbe>()
            .AddSingleton<IKeyProbe<TelegramBotKey>, TelegramBotProbe>();
    }

    // What Telegram issues: the bot's id, a colon, and its secret. Anything else is refused without
    // asking, since it would be put into a URL's path.
    [GeneratedRegex("^[0-9]{1,20}:[A-Za-z0-9_-]{20,100}$", RegexOptions.CultureInvariant)]
    internal static partial Regex TelegramToken();
}

// The common part of every probe that is one HTTP request: the provider's answer read as an outcome,
// and a provider that does not answer in time, or at all, as one that could not be asked. Nothing of
// the request — its URL carries a bot's token — is ever put into what is said.
public abstract class HttpKeyProbe<TKey>(IHttpClientFactory http, AccountsSettings settings) : IKeyProbe<TKey>
{
    public abstract string Provider { get; }

    // The statuses that say the key itself was refused. Every other answer says nothing about it.
    protected virtual IReadOnlyList<HttpStatusCode> Refusals => [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    protected KeyProbeSettings Settings => settings.KeyProbes;

    public virtual async Task<KeyProbeOutcome> ProbeAsync(TKey key, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Settings.Timeout);
        try
        {
            using var request = Request(key);
            using var response = await http.CreateClient(KeyProbes.ClientName).SendAsync(request, deadline.Token);
            return response.IsSuccessStatusCode
                ? new KeyProbeOutcome.Working()
                : Refusals.Contains(response.StatusCode)
                    ? new KeyProbeOutcome.Rejected($"{Provider} refused it ({(int)response.StatusCode})")
                    : new KeyProbeOutcome.Unreachable($"{Provider} answered {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new KeyProbeOutcome.Unreachable($"{Provider} did not answer in time");
        }
        catch (HttpRequestException)
        {
            return new KeyProbeOutcome.Unreachable($"{Provider} could not be reached");
        }
    }

    protected abstract HttpRequestMessage Request(TKey key);

    // Joined as text: a bot's token has a colon in it, which a relative URI's first segment would
    // read as a scheme.
    protected static Uri Under(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");
}

// The key's own metadata, which bills nothing (what the judges' keep-alive pings too).
public sealed class OpenRouterKeyProbe(IHttpClientFactory http, AccountsSettings settings)
    : HttpKeyProbe<OpenRouterKey>(http, settings)
{
    public override string Provider => "OpenRouter";

    protected override HttpRequestMessage Request(OpenRouterKey key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Under(Settings.OpenRouterUrl, "key"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.ApiKey);
        return request;
    }
}

// The token exchange Idealista's search starts with. A well-formed exchange it answers 400 is the
// pair it refused (OAuth's invalid_client), since nothing else in the request is the owner's.
public sealed class IdealistaKeyProbe(IHttpClientFactory http, AccountsSettings settings)
    : HttpKeyProbe<IdealistaKeys>(http, settings)
{
    public override string Provider => "Idealista";

    protected override IReadOnlyList<HttpStatusCode> Refusals =>
        [HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    protected override HttpRequestMessage Request(IdealistaKeys keys)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Under(Settings.IdealistaUrl, "oauth/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "read"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keys.ApiKey}:{keys.Secret}")));
        return request;
    }
}

// The bot the token belongs to. Telegram answers a token it never issued 401, and one it cannot read
// 404.
public sealed class TelegramBotProbe(IHttpClientFactory http, AccountsSettings settings)
    : HttpKeyProbe<TelegramBotKey>(http, settings)
{
    public override string Provider => "Telegram";

    protected override IReadOnlyList<HttpStatusCode> Refusals => [HttpStatusCode.Unauthorized, HttpStatusCode.NotFound];

    public override Task<KeyProbeOutcome> ProbeAsync(TelegramBotKey key, CancellationToken ct) =>
        KeyProbes.TelegramToken().IsMatch(key.Token)
            ? base.ProbeAsync(key, ct)
            : Task.FromResult<KeyProbeOutcome>(new KeyProbeOutcome.Rejected(
                "it is not a Telegram bot token (the bot's id, a colon, and its secret, as BotFather gives it)"));

    protected override HttpRequestMessage Request(TelegramBotKey key) =>
        new(HttpMethod.Get, Under(Settings.TelegramUrl, $"bot{key.Token}/getMe"));
}