using Domain.Contracts;
using Domain.Security.Sealing;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisPersonStateStoreTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static readonly PersonState _state = new(
        """{"cookies":[{"name":"sid","value":"s3cr3t-session","domain":".bank.example"}],"origins":[]}""",
        [new SiteLogin("bank.example", ["bank.example", "login.example"], DateTimeOffset.UnixEpoch, null)]);

    private static SealingKey NewKey() => SealingKey.Generate();

    private static string Person() => $"fran-{Guid.NewGuid():N}";

    [Fact]
    public async Task ASavedRecord_LoadsBackWithTheSameKey()
    {
        var key = NewKey();
        var person = Person();
        await new RedisPersonStateStore(redis.Connection, key).SaveAsync(person, _state);

        var loaded = (await new RedisPersonStateStore(redis.Connection, key).LoadAsync(person)).ShouldNotBeNull();

        loaded.StorageState.ShouldBe(_state.StorageState);
        var login = loaded.Logins.ShouldHaveSingleItem();
        login.Site.ShouldBe("bank.example");
        login.Domains.ShouldBe(["bank.example", "login.example"]);
        login.HandedOffAt.ShouldBe(DateTimeOffset.UnixEpoch);
        login.LastUsedAt.ShouldBeNull();
    }

    // A record the browse server wrote before sealing moved into Domain, under key bytes 0..31 for the
    // user id "user-fran": the tenant kit's move copies records as they are, so it must still load.
    [Fact]
    public async Task ARecordSavedBeforeSealingMoved_StillLoads()
    {
        const string blob =
            "AvImA0QBABqZwbNSZABi8s/zuQjXmlfCvLMtAIDyumDBbfsrenslYQJoxK9w6tGsDBJfu2t2tSKi6qdgxe8XFtCr0hpLGJpvVtJx"
            + "CAYCV1P3pnXTQn1iMSjP8S25BocmYV6oBs2zo7qXqXQHtuD2yav4YVUc6GWQJpL0gzm0ufAe9TELzivABdrXyFRej1Uqw+bNJaQJ"
            + "8NR5BBmJshSHg9NdkqzQ7EPxt3OYedF9aBTiTC0QrKNmx6MnYRzhCTulM4b1w/5D1SSFgjhcbD3qk8kOPqMEHOTgIhmMzbk9J7+g"
            + "jsOYgxAwEN+Pybjlen1WfL3UD5C9S3/eXZ1puIWu36DPwLhwhyZm2zHEBlb2bnkLCp+SgvkLbb3Cz6/f1c7rGxHmsu8tm04n15Zk"
            + "+7BlGs7rvtbBTAxDid9QiIpKWTtTGgJG5lXbSTkr0BC3EEsEZ05ryjJaBoKPtdSfwEPnQIrtyKjB";
        await redis.Connection.GetDatabase().StringSetAsync("websearch:person:user-fran", Convert.FromBase64String(blob));
        var key = SealingKey.Import(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var loaded = (await new RedisPersonStateStore(redis.Connection, key).LoadAsync("user-fran")).ShouldNotBeNull();

        loaded.StorageState.ShouldBe(_state.StorageState);
        loaded.Logins.ShouldHaveSingleItem().Domains.ShouldBe(["bank.example", "login.example"]);
    }

    // The first format listed bare domains rather than logins and was never deployed: such a
    // record is nothing remembered, so the person logs in again rather than being refused.
    [Fact]
    public async Task ARecordOfTheFirstFormat_IsNothingRemembered()
    {
        var person = Person();
        await redis.Connection.GetDatabase().StringSetAsync($"websearch:person:{person}", new byte[] { 1, 0, 0, 0 });

        (await new RedisPersonStateStore(redis.Connection, NewKey()).LoadAsync(person)).ShouldBeNull();
    }

    [Fact]
    public async Task ARecordOfAnUnknownFormat_RefusesToLoad()
    {
        var person = Person();
        await redis.Connection.GetDatabase().StringSetAsync($"websearch:person:{person}", new byte[40]);

        await Should.ThrowAsync<PersonStateUnreadableException>(() =>
            new RedisPersonStateStore(redis.Connection, NewKey()).LoadAsync(person));
    }

    // At rest the record says nothing of what it holds.
    [Fact]
    public async Task TheStoredBytes_CarryNoCookieInTheClear()
    {
        var person = Person();
        await new RedisPersonStateStore(redis.Connection, NewKey()).SaveAsync(person, _state);

        string stored = (await redis.Connection.GetDatabase().StringGetAsync($"websearch:person:{person}"))!;

        stored.ShouldNotContain("s3cr3t-session");
        stored.ShouldNotContain("bank.example");
    }

    [Fact]
    public async Task ARecordUnderAnotherKey_RefusesToLoad_NamingWhy()
    {
        var person = Person();
        await new RedisPersonStateStore(redis.Connection, NewKey()).SaveAsync(person, _state);

        var refused = await Should.ThrowAsync<PersonStateUnreadableException>(() =>
            new RedisPersonStateStore(redis.Connection, NewKey()).LoadAsync(person));

        refused.Message.ShouldContain("does not open");
        refused.Person.ShouldBe(person);
    }

    [Fact]
    public async Task ARecordWithNoKeyConfigured_RefusesToLoad_RatherThanReadingAsNothing()
    {
        var person = Person();
        await new RedisPersonStateStore(redis.Connection, NewKey()).SaveAsync(person, _state);

        var store = new RedisPersonStateStore(redis.Connection, null);

        store.CanWrite.ShouldBeFalse();
        (await Should.ThrowAsync<PersonStateUnreadableException>(() => store.LoadAsync(person)))
            .Message.ShouldContain("no key");
    }

    [Fact]
    public async Task NoRecord_IsNothingRemembered_KeyOrNot()
    {
        (await new RedisPersonStateStore(redis.Connection, null).LoadAsync(Person())).ShouldBeNull();
        (await new RedisPersonStateStore(redis.Connection, NewKey()).LoadAsync(Person())).ShouldBeNull();
    }

    // One person's record moved under another's name does not open: the name is sealed in.
    [Fact]
    public async Task ARecordCopiedUnderAnotherPerson_DoesNotOpen()
    {
        var key = NewKey();
        var fran = Person();
        var ana = Person();
        await new RedisPersonStateStore(redis.Connection, key).SaveAsync(fran, _state);
        var db = redis.Connection.GetDatabase();
        await db.StringSetAsync($"websearch:person:{ana}", await db.StringGetAsync($"websearch:person:{fran}"));

        await Should.ThrowAsync<PersonStateUnreadableException>(() =>
            new RedisPersonStateStore(redis.Connection, key).LoadAsync(ana));
    }
}