using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public sealed class RedisPushSubscriptionStoreTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisPushSubscriptionStore _store = new(fixture.Connection);
    private readonly List<string> _createdUserIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var db = fixture.Connection.GetDatabase();
        foreach (var userId in _createdUserIds)
        {
            // Read endpoints from the user's hash before deleting, to clean up index keys
            var entries = await db.HashGetAllAsync($"push:subs:{userId}");
            foreach (var entry in entries)
            {
                var endpoint = entry.Name.ToString();
                await db.KeyDeleteAsync($"push:ep:{endpoint}");

                // Endpoints can be in multiple space sets — scan and remove from all
                var server = fixture.Connection.GetServer(fixture.Connection.GetEndPoints().First());
                await foreach (var key in server.KeysAsync(pattern: "push:space:*"))
                {
                    await db.SetRemoveAsync(key, endpoint);
                }
            }

            await db.KeyDeleteAsync($"push:subs:{userId}");
        }
    }

    private PushSubscriptionDto CreateSubscription(string endpoint = "https://fcm.googleapis.com/fcm/send/abc123") =>
        new(endpoint, "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUb...", "tBHItJI5svbpC7sc9d8M2w==");

    [Fact]
    public async Task SaveAsync_MultipleDevices_StoresAll()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = CreateSubscription("https://fcm.googleapis.com/fcm/send/device1");
        var sub2 = CreateSubscription("https://fcm.googleapis.com/fcm/send/device2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SaveAsync_SameEndpoint_OverwritesPrevious()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/same", "key1", "auth1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/same", "key2", "auth2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(1);
        userSubs[0].Subscription.P256dh.ShouldBe("key2");
    }

    [Fact]
    public async Task RemoveAsync_ExistingSubscription_RemovesIt()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = CreateSubscription();

        await _store.SaveAsync(userId, sub);
        await _store.RemoveAsync(userId, sub.Endpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.UserId == userId);
    }

    [Fact]
    public async Task RemoveAsync_NonExistentSubscription_DoesNotThrow()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);

        await Should.NotThrowAsync(() => _store.RemoveAsync(userId, "https://nonexistent.example.com"));
    }

    [Fact]
    public async Task RemoveByEndpointAsync_RemovesFromCorrectUser()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);
        var sharedEndpoint = "https://fcm.googleapis.com/fcm/send/shared";

        await _store.SaveAsync(userId1, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId2, CreateSubscription("https://fcm.googleapis.com/fcm/send/other"));

        await _store.RemoveByEndpointAsync(sharedEndpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == sharedEndpoint);
        all.ShouldContain(x => x.UserId == userId2);
    }

    [Fact]
    public async Task GetAllAsync_MultipleUsers_ReturnsAll()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);

        await _store.SaveAsync(userId1, CreateSubscription("https://fcm.googleapis.com/fcm/send/u1d1"));
        await _store.SaveAsync(userId2, CreateSubscription("https://fcm.googleapis.com/fcm/send/u2d1"));

        var all = await _store.GetAllAsync();
        all.ShouldContain(x => x.UserId == userId1);
        all.ShouldContain(x => x.UserId == userId2);
    }

    [Fact]
    public async Task GetBySpaceAsync_FiltersSubscriptionsBySpace()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/space-a", "k1", "a1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/space-b", "k2", "a2");

        await _store.SaveAsync(userId, sub1, "space-a");
        await _store.SaveAsync(userId, sub2, "space-b");

        var spaceASubs = await _store.GetBySpaceAsync("space-a");
        spaceASubs.ShouldContain(x => x.Subscription.Endpoint == sub1.Endpoint);
        spaceASubs.ShouldNotContain(x => x.Subscription.Endpoint == sub2.Endpoint);
    }

    [Fact]
    public async Task GetBySpaceAsync_DefaultSpace_IncludesSubscriptionsWithoutExplicitSpace()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/default-space", "k1", "a1");

        await _store.SaveAsync(userId, sub);

        var defaultSubs = await _store.GetBySpaceAsync("default");
        defaultSubs.ShouldContain(x => x.Subscription.Endpoint == sub.Endpoint);
    }

    [Fact]
    public async Task GetBySpaceAsync_NoMatchingSpace_ReturnsEmpty()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/other-space", "k1", "a1");

        await _store.SaveAsync(userId, sub, "some-space");

        var result = await _store.GetBySpaceAsync("nonexistent-space");
        result.ShouldNotContain(x => x.Subscription.Endpoint == sub.Endpoint);
    }

    [Fact]
    public async Task RemoveByEndpointAsync_EndpointDoesNotExist_DoesNotThrow()
    {
        await Should.NotThrowAsync(
            () => _store.RemoveByEndpointAsync("https://nonexistent.example.com/totally-missing"));
    }

    [Fact]
    public async Task RemoveByEndpointAsync_SharedEndpointAcrossMultipleUsers_RemovesFromAll()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        var userId3 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);
        _createdUserIds.Add(userId3);
        var sharedEndpoint = "https://fcm.googleapis.com/fcm/send/shared-across-all";

        await _store.SaveAsync(userId1, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId2, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId3, CreateSubscription(sharedEndpoint));

        await _store.RemoveByEndpointAsync(sharedEndpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == sharedEndpoint);
    }

    [Fact]
    public async Task SaveAsync_EndpointWithSpecialCharacters_HandlesCorrectly()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://example.com/push/endpoint?token=abc%20def&lang=en&special=<>&quote=\"test\"";
        var sub = new PushSubscriptionDto(endpoint, "key123", "auth456");

        await _store.SaveAsync(userId, sub);

        var all = await _store.GetAllAsync();
        var match = all.FirstOrDefault(x => x.UserId == userId);
        match.Subscription.ShouldNotBeNull();
        match.Subscription.Endpoint.ShouldBe(endpoint);
        match.Subscription.P256dh.ShouldBe("key123");
        match.Subscription.Auth.ShouldBe("auth456");
    }

    [Fact]
    public async Task RemoveByEndpointAsync_UsesIndexForDirectLookup()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://fcm.googleapis.com/fcm/send/indexed";
        var sub = CreateSubscription(endpoint);

        await _store.SaveAsync(userId, sub);

        var db = fixture.Connection.GetDatabase();
        var indexValue = await db.StringGetAsync($"push:ep:{endpoint}");
        indexValue.HasValue.ShouldBeTrue();
        indexValue.ToString().ShouldBe(userId);

        await _store.RemoveByEndpointAsync(endpoint);

        var indexAfter = await db.StringGetAsync($"push:ep:{endpoint}");
        indexAfter.HasValue.ShouldBeFalse();

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }

    [Fact]
    public async Task SaveAsync_EndpointTransferToNewUser_PreservesNewSpaceSet()
    {
        var userA = $"test-user-{Guid.NewGuid():N}";
        var userB = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userA);
        _createdUserIds.Add(userB);
        var endpoint = "https://fcm.googleapis.com/fcm/send/transfer-space";
        var sub = new PushSubscriptionDto(endpoint, "k1", "a1");

        await _store.SaveAsync(userA, sub, "space-old");

        await _store.SaveAsync(userB, sub, "space-new");

        var newSpaceSubs = await _store.GetBySpaceAsync("space-new");
        newSpaceSubs.ShouldContain(x => x.Subscription.Endpoint == endpoint);
        newSpaceSubs.First(x => x.Subscription.Endpoint == endpoint).UserId.ShouldBe(userB);
    }

    [Fact]
    public async Task SaveAsync_SameEndpointDifferentSpaces_AccumulatesInBothSpaceSets()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://fcm.googleapis.com/fcm/send/multi-space";
        var sub = new PushSubscriptionDto(endpoint, "k1", "a1");

        await _store.SaveAsync(userId, sub, "space-a");
        await _store.SaveAsync(userId, sub, "space-b");

        var spaceASubs = await _store.GetBySpaceAsync("space-a");
        spaceASubs.ShouldContain(x => x.Subscription.Endpoint == endpoint,
            "Endpoint should remain in space-a after saving to space-b");

        var spaceBSubs = await _store.GetBySpaceAsync("space-b");
        spaceBSubs.ShouldContain(x => x.Subscription.Endpoint == endpoint,
            "Endpoint should be added to space-b");
    }

    [Fact]
    public async Task SaveAsync_WithReplacingEndpoint_TransfersSpaceMemberships()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var oldEndpoint = "https://fcm.googleapis.com/fcm/send/old-ep";
        var newEndpoint = "https://fcm.googleapis.com/fcm/send/new-ep";
        var oldSub = new PushSubscriptionDto(oldEndpoint, "k1", "a1");
        var newSub = new PushSubscriptionDto(newEndpoint, "k2", "a2");

        // Device visits default then x with old endpoint
        await _store.SaveAsync(userId, oldSub);
        await _store.SaveAsync(userId, oldSub, "x");

        await _store.SaveAsync(userId, newSub, "x", replacingEndpoint: oldEndpoint);

        var defaultSubs = await _store.GetBySpaceAsync("default");
        defaultSubs.ShouldContain(x => x.Subscription.Endpoint == newEndpoint,
            "New endpoint should inherit default space membership from old endpoint");

        var xSubs = await _store.GetBySpaceAsync("x");
        xSubs.ShouldContain(x => x.Subscription.Endpoint == newEndpoint);

        defaultSubs.ShouldNotContain(x => x.Subscription.Endpoint == oldEndpoint);
        xSubs.ShouldNotContain(x => x.Subscription.Endpoint == oldEndpoint);
    }

    [Fact]
    public async Task SaveAsync_WithReplacingEndpoint_CleansUpOldHashAndIndex()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var oldEndpoint = "https://fcm.googleapis.com/fcm/send/old-cleanup";
        var newEndpoint = "https://fcm.googleapis.com/fcm/send/new-cleanup";
        var oldSub = new PushSubscriptionDto(oldEndpoint, "k1", "a1");
        var newSub = new PushSubscriptionDto(newEndpoint, "k2", "a2");

        await _store.SaveAsync(userId, oldSub);
        await _store.SaveAsync(userId, newSub, replacingEndpoint: oldEndpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == oldEndpoint);

        var match = all.First(x => x.UserId == userId);
        match.Subscription.Endpoint.ShouldBe(newEndpoint);
        match.Subscription.P256dh.ShouldBe("k2");
        match.Subscription.Auth.ShouldBe("a2");

        var db = fixture.Connection.GetDatabase();
        var oldIndex = await db.StringGetAsync($"push:ep:{oldEndpoint}");
        oldIndex.HasValue.ShouldBeFalse("Old endpoint index should be cleaned up");
    }

    [Fact]
    public async Task RemoveAsync_CleansUpSpaceSets()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://fcm.googleapis.com/fcm/send/remove-space-cleanup";
        var sub = new PushSubscriptionDto(endpoint, "k1", "a1");

        await _store.SaveAsync(userId, sub, "space-a");
        await _store.SaveAsync(userId, sub, "space-b");

        await _store.RemoveAsync(userId, endpoint);

        // Verify at Redis level — space sets must not contain the endpoint
        var db = fixture.Connection.GetDatabase();
        (await db.SetContainsAsync("push:space:space-a", endpoint)).ShouldBeFalse(
            "Endpoint should be removed from space-a set after RemoveAsync");
        (await db.SetContainsAsync("push:space:space-b", endpoint)).ShouldBeFalse(
            "Endpoint should be removed from space-b set after RemoveAsync");
    }

    [Fact]
    public async Task RemoveByEndpointAsync_CleansUpSpaceSets()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://fcm.googleapis.com/fcm/send/remove-ep-space-cleanup";
        var sub = new PushSubscriptionDto(endpoint, "k1", "a1");

        await _store.SaveAsync(userId, sub, "space-x");
        await _store.SaveAsync(userId, sub, "space-y");

        await _store.RemoveByEndpointAsync(endpoint);

        // Verify at Redis level — space sets must not contain the endpoint
        var db = fixture.Connection.GetDatabase();
        (await db.SetContainsAsync("push:space:space-x", endpoint)).ShouldBeFalse(
            "Endpoint should be removed from space-x set after RemoveByEndpointAsync");
        (await db.SetContainsAsync("push:space:space-y", endpoint)).ShouldBeFalse(
            "Endpoint should be removed from space-y set after RemoveByEndpointAsync");
    }

    [Fact]
    public async Task RemoveAsync_OnlyRemovesTargetEndpoint_LeavesOthersIntact()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/keep", "k1", "a1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/remove", "k2", "a2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);
        await _store.RemoveAsync(userId, sub2.Endpoint);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(1);
        userSubs[0].Subscription.Endpoint.ShouldBe(sub1.Endpoint);
        userSubs[0].Subscription.P256dh.ShouldBe("k1");
        userSubs[0].Subscription.Auth.ShouldBe("a1");
    }
}