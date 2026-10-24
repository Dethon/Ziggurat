using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;
using McpServerSugary.App;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her phone asks to be told when a pen is due: the app reads the server's public key, subscribes
// with the browser's push service and hands the subscription back, with the language the reminders
// are to be written in, since that setting lives on the phone.
public class SugaryPushApiTests
{
    public static readonly WebPushConfig Keys = new()
    {
        PublicKey = "BPublicKeyForTests", PrivateKey = "private", Subject = "mailto:test@example.com"
    };

    private static JsonObject Subscription(string endpoint = "https://push.example.com/a", string language = "es") =>
        new() { ["endpoint"] = endpoint, ["p256dh"] = "key", ["auth"] = "secret", ["language"] = language };

    private static async Task<IReadOnlyList<PushSubscription>> StoredAsync(SugaryServer server)
    {
        await using var db = await server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>()
            .CreateDbContextAsync();
        return await db.PushSubscriptions.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task TheServersKey_IsGivenToThePhone()
    {
        await using var server = await SugaryServer.StartAsync(configure: s => s with { WebPush = Keys });
        var client = await server.SignedInAsync();

        var push = await client.GetFromJsonAsync<JsonNode>("/api/push");

        push!["publicKey"]!.GetValue<string>().ShouldBe(Keys.PublicKey);
    }

    // A deployment with no keys says so, and the app offers no reminders.
    [Fact]
    public async Task WithNoKeys_ThePhoneIsToldThereAreNoReminders()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var push = await client.GetFromJsonAsync<JsonNode>("/api/push");

        push!["publicKey"].ShouldBeNull();
        (await client.PutAsJsonAsync("/api/push/subscription", Subscription())).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    // Subscribed again from the same phone, as the app does each time it opens and when she changes
    // its language: one subscription, in the language last given.
    [Fact]
    public async Task ASubscription_IsKeptOncePerEndpoint()
    {
        await using var server = await SugaryServer.StartAsync(configure: s => s with { WebPush = Keys });
        var client = await server.SignedInAsync();

        (await client.PutAsJsonAsync("/api/push/subscription", Subscription())).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await client.PutAsJsonAsync("/api/push/subscription", Subscription(language: "en"));
        await client.PutAsJsonAsync("/api/push/subscription", Subscription("https://push.example.com/b"));

        (await StoredAsync(server)).Select(s => (s.Endpoint, s.Language)).OrderBy(s => s.Endpoint)
            .ShouldBe([("https://push.example.com/a", "en"), ("https://push.example.com/b", "es")]);
    }

    [Fact]
    public async Task ASubscription_IsRemovedWhenSheTurnsRemindersOff()
    {
        await using var server = await SugaryServer.StartAsync(configure: s => s with { WebPush = Keys });
        var client = await server.SignedInAsync();
        await client.PutAsJsonAsync("/api/push/subscription", Subscription());

        var removed = await client.DeleteAsync("/api/push/subscription?endpoint=https%3A%2F%2Fpush.example.com%2Fa");

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StoredAsync(server)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://push.example.com/a", "es")]
    [InlineData("not a url", "es")]
    [InlineData("https://push.example.com/a", "fr")]
    public async Task ASubscriptionThePushServiceCouldNotHaveMade_IsRefused(string endpoint, string language)
    {
        await using var server = await SugaryServer.StartAsync(configure: s => s with { WebPush = Keys });
        var client = await server.SignedInAsync();

        var refused = await client.PutAsJsonAsync("/api/push/subscription", Subscription(endpoint, language));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>()
            .ShouldBe(PushEndpoints.BadSubscription);
        (await StoredAsync(server)).ShouldBeEmpty();
    }
}