using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Infrastructure.Clients.Accounts;

// The accounts service's internal API, asked with the caller's own MCP credential: whose an identity
// is, signed (the assertion a channel carries on the person's turn), the household's guest for a
// satellite, and for the Telegram bot the ways in it redeems. The service spells its requests and answers
// for itself (it references no project here), so the wire shape is the records below and the
// camelCase the web defaults give both ends. A refusal the service means is a status this maps to an
// outcome; any other is a failure, thrown, so a caller never mistakes an outage for a stranger.
public sealed class AccountsResolverClient(HttpClient http, string credential) : IAccountResolver, ITelegramAccounts
{
    public const string ClientName = "accounts";

    private const string ResolvePath = "accounts/internal/resolve";
    private const string GuestPath = "accounts/internal/guest";
    private const string TelegramPath = "accounts/internal/telegram/";

    public async Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
    {
        using var response = await PostAsync(ResolvePath, new Query(channel, identity), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var answer = await ReadAsync<Answer>(response, ct);
        return new ResolvedAccount(new Account(answer.TenantId, answer.UserId), answer.DisplayName, answer.Assertion);
    }

    public async Task<ResolvedGuest> GuestAsync(string channel, CancellationToken ct)
    {
        using var response = await PostAsync(GuestPath, new GuestQuery(channel), ct);
        var guest = await ReadAsync<Guest>(response, ct);
        return new ResolvedGuest(guest.TenantId, guest.Assertion);
    }

    public async Task<TelegramStart> RedeemStartAsync(long telegramId, string code, CancellationToken ct)
    {
        using var response = await PostAsync(TelegramPath + "start", new StartQuery(telegramId, code), ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return new TelegramStart(TelegramStartOutcome.CodeRefused);
            case HttpStatusCode.Conflict:
                return new TelegramStart(TelegramStartOutcome.TelegramTaken);
        }

        var started = await ReadAsync<Started>(response, ct);
        return new TelegramStart(
            started.Outcome == "invited" ? TelegramStartOutcome.Invited : TelegramStartOutcome.Linked,
            new ResolvedAccount(new Account(started.TenantId, started.UserId), started.DisplayName, started.Assertion));
    }

    public async Task<TelegramWebLink> MintWebLinkAsync(long telegramId, CancellationToken ct)
    {
        using var response = await PostAsync(TelegramPath + "web-link", new PersonQuery(telegramId), ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return new TelegramWebLink(TelegramWebLinkOutcome.NotLinked);
            case HttpStatusCode.Forbidden:
                return new TelegramWebLink(TelegramWebLinkOutcome.Refused);
        }

        var link = await ReadAsync<WebLink>(response, ct);
        return new TelegramWebLink(TelegramWebLinkOutcome.Minted, link.Url, link.ExpiresAt);
    }

    public async Task<TelegramBind> BindGroupAsync(long chatId, string code, CancellationToken ct)
    {
        using var response = await PostAsync(TelegramPath + "bind", new BindQuery(chatId, code), ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return new TelegramBind(TelegramBindOutcome.CodeRefused);
            case HttpStatusCode.Conflict:
                return new TelegramBind(TelegramBindOutcome.BoundElsewhere);
        }

        return new TelegramBind(TelegramBindOutcome.Bound, (await ReadAsync<GroupTenant>(response, ct)).TenantId);
    }

    public async Task<string?> GroupTenantAsync(long chatId, CancellationToken ct)
    {
        using var response = await PostAsync(TelegramPath + "group", new ChatQuery(chatId), ct);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : (await ReadAsync<GroupTenant>(response, ct)).TenantId;
    }

    public async Task<long?> TelegramIdOfAsync(Account account, CancellationToken ct)
    {
        using var response = await PostAsync(TelegramPath + "of-account", new AccountQuery(account.TenantId, account.UserId), ct);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : (await ReadAsync<OfAccount>(response, ct)).TelegramId;
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return await http.SendAsync(request, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new HttpRequestException("The accounts service answered with no body.");
    }

    private sealed record Query(string Channel, string Identity);

    private sealed record Answer(string TenantId, string UserId, string DisplayName, string Assertion);

    private sealed record GuestQuery(string Channel);

    private sealed record Guest(string TenantId, string Assertion);

    private sealed record StartQuery(long TelegramId, string Code);

    private sealed record Started(string Outcome, string TenantId, string UserId, string DisplayName, string Assertion);

    private sealed record PersonQuery(long TelegramId);

    private sealed record WebLink(Uri Url, DateTimeOffset ExpiresAt);

    private sealed record BindQuery(long ChatId, string Code);

    private sealed record ChatQuery(long ChatId);

    private sealed record GroupTenant(string TenantId);

    private sealed record AccountQuery(string TenantId, string UserId);

    private sealed record OfAccount(long TelegramId);
}