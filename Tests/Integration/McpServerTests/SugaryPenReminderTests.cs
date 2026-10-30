using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Push;
using McpServerSugary.Reminders;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her phone is told the day before a pen is due and again on the day, from the reminder hour on the
// home's clock, in the language the phone subscribed with. A Fiasp pen first used on 1 October and
// lasting 28 days is replaced on the 29th.
public class SugaryPenReminderTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private sealed class Harness(SugaryServer server, RecordingPushSender push, FakeTimeProvider clock)
        : IAsyncDisposable
    {
        public SugaryServer Server => server;
        public RecordingPushSender Push => push;

        public PenReminders Reminders => server.Services.GetRequiredService<PenReminders>();

        // A wall time at home, on the day given.
        public void At(int day, int hour) =>
            clock.SetUtcNow(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, 0, 0), Madrid));

        public async Task SubscribeAsync(
            string endpoint = "https://push.example.com/a", string language = "es", Account? person = null)
        {
            var client = server.As(person ?? SugaryServer.Her);
            (await client.PutAsJsonAsync("/api/push/subscription", new
            {
                endpoint,
                p256dh = "key",
                auth = "secret",
                language
            })).EnsureSuccessStatusCode();
        }

        public async Task PenAsync(string kind = "rapid", string insulin = "Fiasp", int days = 28, Account? person = null)
        {
            var client = server.As(person ?? SugaryServer.Her);
            (await client.PostAsJsonAsync("/api/pens", new { kind, insulin, openedOn = "2026-10-01", days }))
                .EnsureSuccessStatusCode();
        }

        public async Task<IReadOnlyList<string>> EndpointsAsync()
        {
            await using var db = await server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>()
                .CreateDbContextAsync();
            return await db.PushSubscriptions.IgnoreQueryFilters().Select(s => s.Endpoint).ToListAsync();
        }

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }

    private static async Task<Harness> StartAsync()
    {
        var push = new RecordingPushSender();
        var clock = new FakeTimeProvider();
        var server = await SugaryServer.StartAsync(
            configure: s => s with { WebPush = SugaryPushApiTests.Keys, PenReminderHour = 9 },
            services: services => services
                .AddSingleton<IPushMessageSender>(push)
                .AddSingleton(sp => ActivatorUtilities.CreateInstance<PenReminders>(sp, new HomeCalendar(clock, Madrid))));
        return new Harness(server, push, clock);
    }

    private static string Title(JsonNode payload) => payload["title"]!.GetValue<string>();
    private static string Body(JsonNode payload) => payload["body"]!.GetValue<string>();

    [Fact]
    public async Task TheDayBefore_SheIsToldFromTheReminderHour()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PenAsync();

        harness.At(28, 8);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        harness.Push.Sent.ShouldBeEmpty();

        harness.At(28, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        var (endpoint, payload) = harness.Push.Sent.ShouldHaveSingleItem();
        endpoint.ShouldBe("https://push.example.com/a");
        Title(payload).ShouldBe("Mañana toca cambiar la pluma de Fiasp");
        Body(payload).ShouldBe("La empezaste el 1 de octubre y dura 28 días.");
        payload["tag"]!.GetValue<string>().ShouldBe("pen-rapid");
    }

    // A pen is one person's, and so is a phone that subscribed: each reminder reaches its pen's
    // owner's phones and nobody else's in the household.
    [Fact]
    public async Task APensReminder_ReachesItsOwnersPhonesOnly()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync("https://push.example.com/hers", "en");
        await harness.SubscribeAsync("https://push.example.com/his", "en", SugaryServer.Him);
        await harness.PenAsync(insulin: "Fiasp");
        await harness.PenAsync(insulin: "Humalog", days: 27, person: SugaryServer.Him);

        harness.At(27, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        harness.At(28, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        harness.Push.Sent.Select(s => $"{s.Endpoint} {Title(s.Payload)}").ShouldBe(
        [
            "https://push.example.com/his Change your Humalog pen tomorrow",
            "https://push.example.com/his Time for a new Humalog pen",
            "https://push.example.com/hers Change your Fiasp pen tomorrow"
        ], ignoreOrder: true);
    }

    // Asked every quarter of an hour, she is told once a day, and once more on the day.
    [Fact]
    public async Task EachReminder_IsSentOnce()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync(language: "en");
        await harness.PenAsync();

        harness.At(28, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        harness.At(29, 10);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        harness.At(29, 18);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        harness.Push.Sent.Select(s => Title(s.Payload))
            .ShouldBe(["Change your Fiasp pen tomorrow", "Time for a new Fiasp pen"]);
        Body(harness.Push.Sent[1].Payload).ShouldBe("It's been 28 days since you started it on 1 October.");
    }

    // With nobody subscribed the reminder waits for a phone, rather than being spent on none.
    [Fact]
    public async Task AReminderNobodyHeard_IsSentWhenAPhoneSubscribes()
    {
        await using var harness = await StartAsync();
        await harness.PenAsync();

        harness.At(29, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);
        await harness.SubscribeAsync();
        harness.At(29, 12);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        Title(harness.Push.Sent.ShouldHaveSingleItem().Payload).ShouldBe("Toca una pluma nueva de Fiasp");
    }

    // A phone that uninstalled the app is forgotten; the others still hear.
    [Fact]
    public async Task ASubscriptionThePushServiceSaysIsGone_IsForgotten()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync("https://push.example.com/old");
        await harness.SubscribeAsync("https://push.example.com/new");
        await harness.PenAsync();
        harness.Push.Gone.Add("https://push.example.com/old");

        harness.At(29, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        harness.Push.Sent.ShouldHaveSingleItem().Endpoint.ShouldBe("https://push.example.com/new");
        (await harness.EndpointsAsync()).ShouldBe(["https://push.example.com/new"]);
    }

    [Fact]
    public async Task EachKindsPen_IsRemindedOnItsOwn()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PenAsync();
        await harness.PenAsync("basal", "Tresiba", 56);

        harness.At(29, 9);
        await harness.Reminders.RemindAsync(CancellationToken.None);

        harness.Push.Sent.Select(s => s.Payload["tag"]!.GetValue<string>()).ShouldBe(["pen-rapid"]);
    }
}