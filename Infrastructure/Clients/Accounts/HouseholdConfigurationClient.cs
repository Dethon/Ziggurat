using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Accounts;

// A household's configuration as this host may read it (.claude/rules/household-config.md): asked of
// the accounts service with the host's own credential, kept for a short while per view and
// household, and dropped the moment the service announces a save of that household on Redis — so a
// key changed by the operator reaches every server within moments, and one whose announcement was
// missed (Redis away, a host with none) within the lifetime.
//
// An answer asked while a save of its household was announced may be older than the save, so it is
// handed back but not kept: what is kept was asked after the last announcement this host heard.
public sealed class HouseholdConfigurationClient(
    HttpClient http,
    string credential,
    Func<string>? agentToken,
    IConnectionMultiplexer? redis,
    TimeProvider clock,
    TimeSpan lifetime,
    ILogger<HouseholdConfigurationClient>? logger = null) : IHouseholdConfiguration, IAsyncDisposable
{
    public const string ClientName = "accounts-household-config";

    // What the views across every household (the Telegram server's bots) are kept under.
    private const string EveryHousehold = "";

    private readonly ConcurrentDictionary<(string View, string Household), Kept> _kept = new();
    private readonly ConcurrentDictionary<string, long> _announced = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _subscribing = new(1, 1);
    private volatile bool _subscribed;
    private DateTimeOffset _nextSubscribeAttempt = DateTimeOffset.MinValue;

    // The agent's token for its own view: it proves the asker is the agent, acting for nobody.
    public static Func<string> AgentToken(CallTokens tokens) => () =>
        tokens.For(IdentityClaims.Nobody(HouseholdConfigProtocol.AgentTokenChannel), TokenAudiences.HouseholdConfiguration);

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        KeptAsync(HouseholdConfigProtocol.AgentView, householdId, async () =>
        {
            var token = agentToken?.Invoke()
                        ?? throw new InvalidOperationException("only the agent reads a household's agent view, with a token of its own");
            var answer = await AskAsync<AgentHouseholdConfig>(
                HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(householdId, token), ct);
            return householdId == HouseholdConfigProtocol.HomeHousehold
                ? answer.IsHome
                    ? answer
                    : throw new HouseholdConfigRefusedException(new HouseholdConfigRefused(
                        HouseholdConfigRefusal.Unproven, $"asked for the home, answered household {answer.HouseholdId}"))
                : Expected(householdId, answer.HouseholdId, answer);
        }, ct);

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        SectionAsync<HouseholdSettings>(HouseholdConfigProtocol.SettingsView, householdId, proof, ct);

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        SectionAsync<IdealistaKeys?>(HouseholdConfigProtocol.IdealistaView, householdId, proof, ct);

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        SectionAsync<SpacesSection>(HouseholdConfigProtocol.SpacesView, householdId, proof, ct);

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        SectionAsync<HouseholdAgentNames>(HouseholdConfigProtocol.AgentNamesView, householdId, proof, ct);

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        KeptAsync(HouseholdConfigProtocol.TelegramBotsView, EveryHousehold, async () =>
            (await AskAsync<HouseholdTelegramBots>(HouseholdConfigProtocol.TelegramBotsView, new HouseholdConfigQuery(), ct)).Bots, ct);

    public async Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct)
    {
        var token = agentToken?.Invoke()
                    ?? throw new InvalidOperationException("only the agent reads every household's people, with a token of its own");
        return (await AskAsync<HouseholdPeople>(HouseholdConfigProtocol.PeopleView, new HouseholdConfigQuery(CallToken: token), ct))
            .Households;
    }

    // This client's handler alone: the multiplexer is the host's, and another listener on the channel
    // is somebody else's to remove.
    public async ValueTask DisposeAsync()
    {
        if (_subscribed && redis is not null)
        {
            await redis.GetSubscriber().UnsubscribeAsync(RedisChannel.Literal(HouseholdConfigProtocol.SavedChannel), Heard);
        }

        _subscribing.Dispose();
    }

    private Task<T> SectionAsync<T>(string view, string householdId, HouseholdProof proof, CancellationToken ct) =>
        KeptAsync(view, householdId, async () =>
        {
            var answer = await AskAsync<HouseholdConfigAnswer<T>>(
                view, new HouseholdConfigQuery(CallToken: proof.CallToken, Assertion: proof.Assertion, Delegation: proof.Delegation), ct);
            return Expected(householdId, answer.HouseholdId, answer.Section);
        }, ct);

    private async Task<T> KeptAsync<T>(string view, string household, Func<Task<T>> ask, CancellationToken ct)
    {
        await SubscribeAsync(ct);
        if (_kept.TryGetValue((view, household), out var kept) && kept.Until > clock.GetUtcNow())
        {
            return (T)kept.Value!;
        }

        var announced = Announced(household);
        var value = await ask();
        if (Announced(household) == announced)
        {
            _kept[(view, household)] = new Kept(value, clock.GetUtcNow() + lifetime);
        }

        return value;
    }

    // The service answers the household the proof named, and a server serving one household must
    // never be handed another's section under its name.
    private static T Expected<T>(string expected, string answered, T value) =>
        answered == expected
            ? value
            : throw new HouseholdConfigRefusedException(new HouseholdConfigRefused(HouseholdConfigRefusal.Unproven,
                $"the proof names household {answered}, not {expected}"));

    private async Task<T> AskAsync<T>(string view, HouseholdConfigQuery query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HouseholdConfigProtocol.PathOf(view).TrimStart('/'))
        {
            Content = JsonContent.Create(query)
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new HouseholdConfigRefusedException(await RefusalAsync(response, ct)
                                                      ?? new HouseholdConfigRefused(HouseholdConfigRefusal.BadQuery, $"{(int)response.StatusCode}"));
        }

        if (!response.IsSuccessStatusCode)
        {
            var refused = await RefusalAsync(response, ct);
            throw new HttpRequestException(
                $"The accounts service could not answer the {view} view ({(int)response.StatusCode}){(refused is null ? "" : $": {refused.Detail}")}",
                null, response.StatusCode);
        }

        return (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, ct))!;
    }

    private static async Task<HouseholdConfigRefused?> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<HouseholdConfigRefused>(JsonSerializerOptions.Web, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Before the first answer is kept, so no announcement falls between asking and listening. A Redis
    // that cannot be reached is tried again once a lifetime has gone by rather than on every ask;
    // until then, as on a host with no Redis at all, what is kept is dropped by its lifetime alone.
    private async Task SubscribeAsync(CancellationToken ct)
    {
        if (_subscribed || redis is null || clock.GetUtcNow() < _nextSubscribeAttempt)
        {
            return;
        }

        await _subscribing.WaitAsync(ct);
        try
        {
            if (_subscribed)
            {
                return;
            }

            await redis.GetSubscriber().SubscribeAsync(
                RedisChannel.Literal(HouseholdConfigProtocol.SavedChannel), Heard);
            _subscribed = true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _nextSubscribeAttempt = clock.GetUtcNow() + lifetime;
            logger?.LogWarning(ex, "Could not listen for household configuration saves; nothing is kept until it can");
        }
        finally
        {
            _subscribing.Release();
        }
    }

    private void Heard(RedisChannel _, RedisValue message)
    {
        HouseholdConfigSaved? saved;
        try
        {
            saved = JsonSerializer.Deserialize<HouseholdConfigSaved>(message.ToString(), JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return;
        }

        if (saved is not { HouseholdId.Length: > 0 })
        {
            return;
        }

        Drop(saved.HouseholdId);
        if (saved.Section == HouseholdSections.Keys)
        {
            Drop(EveryHousehold);
        }
    }

    // The home asked by name is kept under its name, and a save names the home by its id, which this
    // host may not have been told: any household's save drops it, a re-ask on a save of another.
    private void Drop(string household)
    {
        foreach (var dropped in (string[])[household, HouseholdConfigProtocol.HomeHousehold])
        {
            _announced.AddOrUpdate(dropped, 1, (_, count) => count + 1);
            foreach (var key in _kept.Keys.Where(key => key.Household == dropped))
            {
                _kept.TryRemove(key, out _);
            }
        }
    }

    private long Announced(string household) => _announced.GetValueOrDefault(household);

    private sealed record Kept(object? Value, DateTimeOffset Until);
}