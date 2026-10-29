using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Infrastructure.Clients.Browser;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A person's logins over a context faked in memory and a real sealed store: what a handoff becomes
// when it is settled, what a save writes, and what a Forget takes. A login is a record — the site
// handed to web_login and every domain its handoff touched — and a save keeps only what a
// remembered login names.
public class PersonLoginsTests(RedisFixture redis) : IClassFixture<RedisFixture>, IDisposable
{
    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(5);

    private readonly FakePersonContexts _contexts = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly string _person = $"fran-{Guid.NewGuid():N}";
    private RedisPersonStateStore? _store;
    private PersonLogins? _logins;

    private RedisPersonStateStore Store => _store ??= new RedisPersonStateStore(redis.Connection, RandomNumberGenerator.GetBytes(32));

    private long _maxRecordBytes = 8 * 1024 * 1024;

    private PersonLogins Logins => _logins ??=
        new PersonLogins(_contexts, Store, _time, _debounce, _maxRecordBytes, NullLogger<PersonLogins>.Instance);

    public void Dispose() => _logins?.Dispose();

    // A handoff that sets its cookies while it is open, as the person's typing would.
    private async Task HandOffAsync(
        string url, IReadOnlyCollection<string> visited, bool remember, Action? whileOpen = null)
    {
        await using var lease = await Logins.BeginHandoffAsync(_person, url);
        whileOpen?.Invoke();
        await lease.CompleteAsync(visited, remember);
    }

    private async Task<PersonState> RecordAsync() => (await Store.LoadAsync(_person)).ShouldNotBeNull();

    private static IReadOnlyList<string> CookieValues(string state) =>
        JsonNode.Parse(state)!["cookies"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()).ToList();

    private async Task<IReadOnlyList<string>> SitesAsync() => (await Logins.LoginsAsync(_person)).Select(l => l.Site).ToList();

    [Fact]
    public async Task ATickedDone_RemembersTheSiteWithTheDomainsItsHandoffVisited()
    {
        await HandOffAsync("https://www.youtube.test/login", ["www.youtube.test", "accounts.google.test"], remember: true,
            () =>
            {
                _contexts.SetCookie("sid", ".youtube.test", "yt-session");
                _contexts.SetCookie("idp", ".google.test", "g1");
            });

        var login = (await RecordAsync()).Logins.ShouldHaveSingleItem();
        login.Site.ShouldBe("youtube.test");
        login.Domains.ShouldBe(["google.test", "youtube.test"]);
        CookieValues((await RecordAsync()).StorageState).ShouldBe(["yt-session", "g1"], ignoreOrder: true);
    }

    // The single sign-on case: an unticked login to the shop through the same identity provider
    // as a remembered login must not un-remember the provider, or the remembered one is logged out
    // at the next restart.
    [Fact]
    public async Task AnUntickedLoginThroughASharedProvider_LeavesTheOtherLoginsProviderRememberedAndSaved()
    {
        await HandOffAsync("https://youtube.test/login", ["youtube.test", "accounts.google.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".youtube.test", "yt-session");
            _contexts.SetCookie("idp", ".google.test", "g1");
        });

        await HandOffAsync("https://shop.test/login", ["shop.test", "accounts.google.test"], remember: false, () =>
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
            _contexts.SetCookie("idp", ".google.test", "g2");
        });

        var record = await RecordAsync();
        record.Logins.ShouldHaveSingleItem().Site.ShouldBe("youtube.test");
        record.Logins.Single().Domains.ShouldContain("google.test");
        var saved = CookieValues(record.StorageState);
        saved.ShouldContain("yt-session");
        saved.ShouldContain("g2");
        saved.ShouldNotContain("shop-session");

        var listed = await Logins.LoginsAsync(_person);
        listed.Select(l => (l.Site, l.Remembered)).ShouldBe([("shop.test", false), ("youtube.test", true)]);
    }

    // A cookie an iframe set on a host the main frame never visited is outside every remembered
    // login, so no save writes it.
    [Fact]
    public async Task AThirdPartyCookieSetDuringAnUntickedHandoff_IsNotSaved()
    {
        await HandOffAsync("https://bank.test/login", ["bank.test"], remember: true,
            () => _contexts.SetCookie("sid", ".bank.test", "bank-session"));

        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: false, () =>
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
            _contexts.SetCookie("sso", ".sso-widget.test", "widget-session");
        });

        CookieValues((await RecordAsync()).StorageState).ShouldBe(["bank-session"]);
        (await Logins.LoginsAsync(_person)).Single(l => l.Site == "shop.test").Domains
            .ShouldBe(["shop.test", "sso-widget.test"]);
    }

    // The other half of the cookie diff: a ticked login that needs a cookie its iframe set keeps
    // that domain, though the main frame never went there.
    [Fact]
    public async Task ACookieSetOffTheMainFrameDuringATickedHandoff_JoinsTheLogin()
    {
        _contexts.SetCookie("prefs", ".elsewhere.test", "set-before");

        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
            _contexts.SetCookie("sso", ".sso-widget.test", "widget-session");
        });

        var record = await RecordAsync();
        record.Logins.Single().Domains.ShouldBe(["shop.test", "sso-widget.test"]);
        CookieValues(record.StorageState).ShouldBe(["shop-session", "widget-session"]);
    }

    [Fact]
    public async Task AnUntickedDone_ScrubsAnEarlierRememberedLoginOfTheSameSite_AndNoOther()
    {
        await HandOffAsync("https://bank.test/login", ["bank.test"], remember: true,
            () => _contexts.SetCookie("sid", ".bank.test", "bank-session"));
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
            _contexts.SetOrigin("https://shop.test", "shop-storage");
        });

        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: false);

        var record = await RecordAsync();
        record.Logins.Select(l => l.Site).ShouldBe(["bank.test"]);
        CookieValues(record.StorageState).ShouldBe(["bank-session"]);
        record.StorageState.ShouldNotContain("shop-storage");
        (await Logins.LoginsAsync(_person)).Select(l => (l.Site, l.Remembered))
            .ShouldBe([("bank.test", true), ("shop.test", false)]);
    }

    // Two logins through one provider: forgetting the shop keeps the provider's session the other
    // still signs in with, and strips the shop's own.
    [Fact]
    public async Task ForgetOfOneLogin_KeepsADomainAnotherStillUses_AndStripsOneNobodyDoes()
    {
        await HandOffAsync("https://youtube.test/login", ["youtube.test", "accounts.google.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".youtube.test", "yt-session");
            _contexts.SetCookie("idp", ".google.test", "g1");
        });
        await HandOffAsync("https://shop.test/login", ["shop.test", "accounts.google.test"], remember: true,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));

        (await Logins.ForgetAsync(_person, "shop.test")).ShouldBeTrue();

        CookieValues(_contexts.Live!).ShouldBe(["yt-session", "g1"], ignoreOrder: true);
        var record = await RecordAsync();
        record.Logins.Select(l => l.Site).ShouldBe(["youtube.test"]);
        CookieValues(record.StorageState).ShouldBe(["yt-session", "g1"], ignoreOrder: true);
        (await SitesAsync()).ShouldBe(["youtube.test"]);
    }

    [Fact]
    public async Task ForgetOfALiveOnlyLogin_StripsItFromTheLiveContext()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: false,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));

        (await Logins.ForgetAsync(_person, "https://shop.test/")).ShouldBeTrue();

        CookieValues(_contexts.Live!).ShouldBeEmpty();
        (await SitesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Forget_IsRefusedWhileOneOfTheirHandoffsIsOpen()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));
        await using var open = await Logins.BeginHandoffAsync(_person, "https://bank.test/login");

        (await Logins.ForgetAsync(_person, "shop.test")).ShouldBeFalse();

        await open.AbandonAsync([]);
        (await Logins.ForgetAsync(_person, "shop.test")).ShouldBeTrue();
    }

    // The first settle wins: a Done that lost a race to the expiry, or an expiry after a Done,
    // changes nothing and cannot release the handoff twice.
    [Fact]
    public async Task ALease_SettledTwice_KeepsTheFirstSettle()
    {
        var lease = await Logins.BeginHandoffAsync(_person, "https://shop.test/login");
        _contexts.SetCookie("sid", ".shop.test", "shop-session");

        await lease.CompleteAsync(["shop.test"], remember: true);
        await lease.AbandonAsync(["shop.test"]);
        await lease.CompleteAsync(["shop.test"], remember: false);
        await lease.DisposeAsync();

        lease.IsSettled.ShouldBeTrue();
        (await Logins.LoginsAsync(_person)).ShouldHaveSingleItem().Remembered.ShouldBeTrue();
        (await RecordAsync()).Logins.ShouldHaveSingleItem().Site.ShouldBe("shop.test");
    }

    // A lease nobody settled — an exception on the way out of an exit path — is abandoned when it
    // is disposed, so the person is not left with a handoff open for ever and no save running.
    [Fact]
    public async Task ALease_DisposedUnsettled_IsAbandoned()
    {
        await using (await Logins.BeginHandoffAsync(_person, "https://shop.test/login"))
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
        }

        (await Logins.LoginsAsync(_person)).ShouldHaveSingleItem().Remembered.ShouldBeFalse();
        (await Logins.ForgetAsync(_person, "shop.test")).ShouldBeTrue();
    }

    [Fact]
    public async Task AnAbandonedHandoff_IsLiveOnly_UnlessTheSiteIsAlreadyRemembered()
    {
        await HandOffAsync("https://bank.test/login", ["bank.test"], remember: true,
            () => _contexts.SetCookie("sid", ".bank.test", "bank-session"));

        var bank = await Logins.BeginHandoffAsync(_person, "https://bank.test/login");
        await bank.AbandonAsync(["bank.test"]);
        var shop = await Logins.BeginHandoffAsync(_person, "https://shop.test/login");
        await shop.AbandonAsync(["shop.test"]);

        (await Logins.LoginsAsync(_person)).Select(l => (l.Site, l.Remembered))
            .ShouldBe([("bank.test", true), ("shop.test", false)]);
    }

    // A handoff that never got as far as a page — its tab would not open — left nothing to log
    // into, so it is no login at all.
    [Fact]
    public async Task AnAbandonedHandoffThatTouchedNothing_IsNoLogin()
    {
        var lease = await Logins.BeginHandoffAsync(_person, "https://shop.test/login");

        await lease.AbandonAsync([]);

        (await Logins.LoginsAsync(_person)).ShouldBeEmpty();
        (await Logins.HasLoginAsync(_person, "https://shop.test/")).ShouldBeFalse();
    }

    // The context is made after the lease is taken on a person's first use, restored from the
    // record: what it restores was there before the handoff and is not the handoff's.
    [Fact]
    public async Task ACookieRestoredFromTheRecord_IsNotCountedAsTheHandoffs()
    {
        await HandOffAsync("https://youtube.test/login", ["youtube.test", "accounts.google.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".youtube.test", "yt-session");
            _contexts.SetCookie("idp", ".google.test", "g1");
        });
        var restored = (await RecordAsync()).StorageState;
        _contexts.RaiseContextsClosed();

        var lease = await Logins.BeginHandoffAsync(_person, "https://shop.test/login");
        await _contexts.ReplaceStateAsync(_person, restored);
        _contexts.SetCookie("sid", ".shop.test", "shop-session");
        await lease.CompleteAsync(["shop.test"], remember: false);

        (await Logins.LoginsAsync(_person)).Single(l => l.Site == "shop.test").Domains.ShouldBe(["shop.test"]);
    }

    [Fact]
    public async Task ContextsClosing_ForgetsTheLiveOnlyLogins()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: false,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));

        _contexts.RaiseContextsClosed();

        (await SitesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task HasLogin_AnswersForTheSiteAndEveryDomainItsLoginCovers_RememberedOrLiveOnly()
    {
        await HandOffAsync("https://youtube.test/login", ["youtube.test", "accounts.google.test"], remember: true,
            () => _contexts.SetCookie("sid", ".youtube.test", "yt-session"));
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: false,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));

        (await Logins.HasLoginAsync(_person, "https://www.youtube.test/feed")).ShouldBeTrue();
        (await Logins.HasLoginAsync(_person, "https://mail.google.test/")).ShouldBeTrue();
        (await Logins.HasLoginAsync(_person, "https://shop.test/orders")).ShouldBeTrue();
        (await Logins.HasLoginAsync(_person, "https://bank.test/")).ShouldBeFalse();
        (await Logins.HasLoginAsync($"ana-{Guid.NewGuid():N}", "https://youtube.test/")).ShouldBeFalse();
    }

    [Fact]
    public async Task UseOfARememberedSite_IsSavedOnceTheDebounceRunsOut_WithItsLastUse()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));
        _contexts.SetCookie("sid", ".shop.test", "rotated-session");

        _contexts.RaiseActivity(_person, "https://shop.test/orders");
        _time.Advance(_debounce);

        await Eventually.Until(async () => CookieValues((await RecordAsync()).StorageState).Contains("rotated-session"),
            "the rotated cookie to be saved");
        (await RecordAsync()).Logins.Single().LastUsedAt.ShouldNotBeNull();
    }

    // The login page's own requests must not arm a save while the person is still typing.
    [Fact]
    public async Task NothingIsSavedWhileAHandoffIsOpen()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true,
            () => _contexts.SetCookie("sid", ".shop.test", "shop-session"));
        await using var open = await Logins.BeginHandoffAsync(_person, "https://shop.test/login");
        _contexts.SetCookie("sid", ".shop.test", "half-made");

        _contexts.RaiseActivity(_person, "https://shop.test/login?step=2");
        _time.Advance(_debounce);

        await Eventually.Settle();
        CookieValues((await RecordAsync()).StorageState).ShouldBe(["shop-session"]);
    }

    // The save after use exists so rotated cookies survive a restart: it exports cookies alone,
    // and the origins the last kept login exported stand.
    [Fact]
    public async Task TheSaveAfterUse_WritesCookiesOnly_AndKeepsTheStoredOrigins()
    {
        await HandOffAsync("https://shop.test/login", ["shop.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".shop.test", "shop-session");
            _contexts.SetOrigin("https://shop.test", "stored-at-done");
        });
        var fullExports = _contexts.FullExports;
        _contexts.SetCookie("sid", ".shop.test", "rotated-session");
        _contexts.SetOrigin("https://shop.test", "changed-since");

        _contexts.RaiseActivity(_person, "https://shop.test/orders");
        _time.Advance(_debounce);

        await Eventually.Until(async () => CookieValues((await RecordAsync()).StorageState).Contains("rotated-session"),
            "the rotated cookie to be saved");
        var record = await RecordAsync();
        record.StorageState.ShouldContain("stored-at-done");
        record.StorageState.ShouldNotContain("changed-since");
        _contexts.FullExports.ShouldBe(fullExports);
    }

    // A mail client's IndexedDB runs to tens of megabytes: over the cap it goes first, and the
    // session itself is saved regardless.
    [Fact]
    public async Task ARecordOverTheCap_DropsIndexedDbFirst_AndStillSavesTheCookies()
    {
        _maxRecordBytes = 4_000;

        await HandOffAsync("https://mail.test/login", ["mail.test"], remember: true, () =>
        {
            _contexts.SetCookie("sid", ".mail.test", "mail-session");
            _contexts.SetOrigin("https://mail.test", "prefs-kept", indexedDbBytes: 10_000);
        });

        var state = JsonNode.Parse((await RecordAsync()).StorageState)!;
        CookieValues(state.ToJsonString()).ShouldBe(["mail-session"]);
        var origin = state["origins"]!.AsArray().ShouldHaveSingleItem()!;
        origin["indexedDB"].ShouldBeNull();
        origin.ToJsonString().ShouldContain("prefs-kept");
    }
}