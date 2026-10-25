using System.Security.Cryptography;
using Domain.Contracts;
using Domain.Judgments;
using Infrastructure.Clients.Browser;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Remembered logins against real Camoufox. A second browser over the same Camoufox is a reconnect:
// it holds none of the first one's contexts, so whatever it is logged into came from the store.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class RememberedLoginsBrowserTests(IsolatedSessionBrowserFixture fixture, RedisFixture redis)
    : IClassFixture<RedisFixture>
{
    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(5);

    // /login?as=<name> logs in by script, as a site's own sign-in leaves its cookie; /rotate
    // replaces the session cookie the way a site rotates one; /whoami prints the cookies.
    private static Task ServeAsync(IRoute route)
    {
        var uri = new Uri(route.Request.Url);
        var body = uri.AbsolutePath switch
        {
            "/login" => $"<script>document.cookie='session={uri.Query.Split('=')[^1]}; path=/; max-age=86400'; localStorage.setItem('ls','kept')</script><p>in</p>",
            "/rotate" => "<script>document.cookie='session=rotated; path=/; max-age=86400'</script><p>rotated</p>",
            _ => "<p id='c'></p><script>document.getElementById('c').textContent='cookies:'+(document.cookie||'none')+' ls:'+(localStorage.getItem('ls')||'none')</script>"
        };
        return route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = $"<!DOCTYPE html><html><body>{body}</body></html>"
        });
    }

    private sealed class Rig(PlaywrightWebBrowser browser, PersonLogins logins, FakeTimeProvider time) : IAsyncDisposable
    {
        public PlaywrightWebBrowser Browser { get; } = browser;
        public PersonLogins Logins { get; } = logins;
        public FakeTimeProvider Time { get; } = time;

        public async ValueTask DisposeAsync()
        {
            Logins.Dispose();
            await Browser.DisposeAsync();
        }
    }

    private Rig NewBrowser(IPersonStateStore store)
    {
        var browser = new PlaywrightWebBrowser(wsEndpoint: fixture.WsEndpoint, personStates: store);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return new Rig(browser, new PersonLogins(browser, store, time, _debounce, NullLogger<PersonLogins>.Instance), time);
    }

    private static async Task<string> WhoAmIAsync(PlaywrightWebBrowser browser, string site, string person)
    {
        await browser.RouteOnContextAsync($"{site}/**", ServeAsync, person);
        var result = await browser.NavigateAsync(new BrowseRequest($"probe-{Guid.NewGuid():N}", $"{site}/whoami?{Guid.NewGuid():N}")
        {
            Caller = JudgmentCaller.None,
            Person = person
        });
        result.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
        return result.Content ?? "";
    }

    private static async Task HandOffAsync(Rig rig, string site, string person, string name, bool remember)
    {
        await rig.Browser.RouteOnContextAsync($"{site}/**", ServeAsync, person);
        var handoff = await rig.Browser.OpenHandoffAsync(person, $"{site}/login?as={name}");
        rig.Logins.BeginHandoff(person);
        var hosts = rig.Browser.HandoffVisitedHosts(handoff);
        await rig.Browser.CloseHandoffAsync(handoff);
        await rig.Logins.CompleteHandoffAsync(person, hosts, remember);
    }

    private RedisPersonStateStore Store(byte[] key) => new(redis.Connection, key);

    private static string Site() => $"https://remembered-{Guid.NewGuid():N}.test";

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ARememberedLogin_IsLoggedInAfterTheBrowserReconnects_StorageIncluded()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var store = Store(RandomNumberGenerator.GetBytes(32));
        var person = $"fran-{Guid.NewGuid():N}";
        var site = Site();
        await using (var first = NewBrowser(store))
        {
            await HandOffAsync(first, site, person, "fran", remember: true);
        }

        await using var second = NewBrowser(store);
        var page = await WhoAmIAsync(second.Browser, site, person);

        page.ShouldContain("session=fran");
        page.ShouldContain("ls:kept");
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnUntickedLogin_LastsUntilTheBrowserRestarts_AndNoSaveCarriesIt()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var store = Store(RandomNumberGenerator.GetBytes(32));
        var person = $"fran-{Guid.NewGuid():N}";
        var sensitive = Site();
        var ordinary = Site();
        await using (var first = NewBrowser(store))
        {
            await HandOffAsync(first, sensitive, person, "secret", remember: false);
            (await WhoAmIAsync(first.Browser, sensitive, person)).ShouldContain("session=secret");

            // A later remembered login saves the context again, and leaves the unticked one out.
            await HandOffAsync(first, ordinary, person, "fran", remember: true);
        }

        var record = (await store.LoadAsync(person)).ShouldNotBeNull();
        record.StorageState.ShouldNotContain("secret");
        record.StorageState.ShouldContain(new Uri(ordinary).Host);

        await using var second = NewBrowser(store);
        (await WhoAmIAsync(second.Browser, sensitive, person)).ShouldNotContain("session=secret");
        (await WhoAmIAsync(second.Browser, ordinary, person)).ShouldContain("session=fran");
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ACookieTheSiteRotatesWhileBrowsedAsThePerson_IsInTheNextRestore()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var store = Store(RandomNumberGenerator.GetBytes(32));
        var person = $"fran-{Guid.NewGuid():N}";
        var site = Site();
        await using (var first = NewBrowser(store))
        {
            await HandOffAsync(first, site, person, "fran", remember: true);
            var rotated = await first.Browser.NavigateAsync(new BrowseRequest($"s-{Guid.NewGuid():N}", $"{site}/rotate")
            {
                Caller = JudgmentCaller.None,
                Person = person
            });
            rotated.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

            first.Time.Advance(_debounce);
            await Eventually.Until(
                () => store.LoadAsync(person).GetAwaiter().GetResult()!.StorageState.Contains("rotated"),
                "the rotated cookie to be saved");
        }

        await using var second = NewBrowser(store);
        (await WhoAmIAsync(second.Browser, site, person)).ShouldContain("session=rotated");
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task SavedLoginsUnderAnotherKey_RefuseTheBrowse_RatherThanBrowsingLoggedOut()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var person = $"fran-{Guid.NewGuid():N}";
        var site = Site();
        await using (var first = NewBrowser(Store(RandomNumberGenerator.GetBytes(32))))
        {
            await HandOffAsync(first, site, person, "fran", remember: true);
        }

        await using var second = NewBrowser(Store(RandomNumberGenerator.GetBytes(32)));
        var refused = await second.Browser.NavigateAsync(new BrowseRequest($"s-{Guid.NewGuid():N}", $"{site}/whoami")
        {
            Caller = JudgmentCaller.None,
            Person = person
        });

        refused.Status.ShouldBe(BrowseStatus.LoginsUnreadable);
        refused.ErrorMessage.ShouldNotBeNull().ShouldContain("does not open");
    }
}