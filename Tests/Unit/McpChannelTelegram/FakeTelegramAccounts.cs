using System.Collections.Concurrent;
using System.Globalization;
using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelTelegram;

// The accounts service as the bot meets it, in memory: who each Telegram id is, the codes an owner or
// a person's account page handed out, and which household each group is bound to. A code is spent
// by the first Telegram that redeems it, as the service spends it; anything it would refuse it
// refuses with the same outcome. It counts the questions it is asked, so a test can tell a cached
// answer from a fresh one, and it can be made unreachable, as the service is in an outage.
internal sealed class FakeTelegramAccounts : IAccountResolver, ITelegramAccounts
{
    public const string Household = "household-home";

    private readonly ConcurrentDictionary<long, ResolvedAccount> _people = new();
    private readonly ConcurrentDictionary<long, string> _groups = new();
    private readonly ConcurrentDictionary<string, Func<long, TelegramStart>> _startCodes = new();
    private readonly ConcurrentDictionary<string, string> _groupCodes = new();
    private readonly ConcurrentDictionary<long, byte> _operators = new();
    private int _resolutions;
    private int _webLinks;

    public bool Unreachable { get; set; }

    public int Resolutions => _resolutions;

    public int WebLinksMinted => _webLinks;

    public static ResolvedAccount AccountOf(string name, string household = Household) =>
        new(new Account(household, $"user-{name}"), name);

    public ResolvedAccount Link(long telegramId, string name, string household = Household) =>
        _people[telegramId] = AccountOf(name, household);

    public void MakeOperator(long telegramId) => _operators[telegramId] = 0;

    public void Bind(long chatId, string household = Household) => _groups[chatId] = household;

    // The first Telegram to send it becomes the person it names, unless that Telegram is somebody's.
    public string Invite(string name, string household = Household) =>
        StartCode((code, telegramId) => _people.ContainsKey(telegramId)
            ? new TelegramStart(TelegramStartOutcome.TelegramTaken)
            : Spend(code, new TelegramStart(TelegramStartOutcome.Invited, Link(telegramId, name, household))));

    // The account page's code: links its person to the Telegram that sends it, unless that Telegram
    // is somebody else's.
    public string LinkCode(string name, string household = Household) =>
        StartCode((code, telegramId) => _people.TryGetValue(telegramId, out var holder) && holder.DisplayName != name
            ? new TelegramStart(TelegramStartOutcome.TelegramTaken)
            : Spend(code, new TelegramStart(TelegramStartOutcome.Linked, Link(telegramId, name, household))));

    public string GroupCode(string household = Household)
    {
        var code = NewCode();
        _groupCodes[code] = household;
        return code;
    }

    public Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
    {
        ThrowIfUnreachable();
        Interlocked.Increment(ref _resolutions);
        return Task.FromResult(channel == "telegram" && _people.TryGetValue(long.Parse(identity, CultureInfo.InvariantCulture), out var person)
            ? person
            : null);
    }

    public Task<TelegramStart> RedeemStartAsync(long telegramId, string code, CancellationToken ct)
    {
        ThrowIfUnreachable();
        return Task.FromResult(_startCodes.TryGetValue(code, out var redeem)
            ? redeem(telegramId)
            : new TelegramStart(TelegramStartOutcome.CodeRefused));
    }

    public Task<TelegramWebLink> MintWebLinkAsync(long telegramId, CancellationToken ct)
    {
        ThrowIfUnreachable();
        if (!_people.ContainsKey(telegramId))
        {
            return Task.FromResult(new TelegramWebLink(TelegramWebLinkOutcome.NotLinked));
        }

        if (_operators.ContainsKey(telegramId))
        {
            return Task.FromResult(new TelegramWebLink(TelegramWebLinkOutcome.Refused));
        }

        Interlocked.Increment(ref _webLinks);
        return Task.FromResult(new TelegramWebLink(
            TelegramWebLinkOutcome.Minted,
            new Uri($"https://assistants.example/accounts/enroll/{NewCode()}"),
            new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
    }

    public Task<TelegramBind> BindGroupAsync(long chatId, string code, CancellationToken ct)
    {
        ThrowIfUnreachable();
        if (!_groupCodes.TryGetValue(code, out var household))
        {
            return Task.FromResult(new TelegramBind(TelegramBindOutcome.CodeRefused));
        }

        if (_groups.TryGetValue(chatId, out var boundTo) && boundTo != household)
        {
            return Task.FromResult(new TelegramBind(TelegramBindOutcome.BoundElsewhere));
        }

        _groupCodes.TryRemove(code, out _);
        _groups[chatId] = household;
        return Task.FromResult(new TelegramBind(TelegramBindOutcome.Bound, household));
    }

    public Task<string?> GroupTenantAsync(long chatId, CancellationToken ct)
    {
        ThrowIfUnreachable();
        return Task.FromResult(_groups.GetValueOrDefault(chatId));
    }

    public Task<long?> TelegramIdOfAsync(Account account, CancellationToken ct)
    {
        ThrowIfUnreachable();
        return Task.FromResult(_people
            .Where(p => p.Value.Account == account)
            .Select(p => (long?)p.Key)
            .FirstOrDefault());
    }

    private string StartCode(Func<string, long, TelegramStart> redeem)
    {
        var code = NewCode();
        _startCodes[code] = telegramId => redeem(code, telegramId);
        return code;
    }

    private TelegramStart Spend(string code, TelegramStart redeemed)
    {
        _startCodes.TryRemove(code, out _);
        return redeemed;
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
        {
            throw new HttpRequestException("the accounts service is down");
        }
    }

    private static string NewCode() => Guid.NewGuid().ToString("N");
}