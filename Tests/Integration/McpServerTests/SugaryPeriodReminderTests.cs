using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Push;
using McpServerSugary.Reminders;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A period reminder is hers to turn on: off until she does, since it shows on a lock screen. On, her
// phones are told three days before the start her cycles make likeliest (the app's forecast), from the
// reminder hour on her household's clock, once. Starts on 9 September and 8 October are a 29-day cycle:
// the next most likely on 6 November, told from 3 November.
public class SugaryPeriodReminderTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private sealed class Harness(SugaryServer server, RecordingPushSender push, FakeTimeProvider clock) : IAsyncDisposable
    {
        public SugaryServer Server => server;
        public RecordingPushSender Push => push;

        public PeriodReminders Reminders => server.Services.GetRequiredService<PeriodReminders>();

        public void At(int month, int day, int hour) =>
            clock.SetUtcNow(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, month, day, hour, 0, 0), _madrid));

        public async Task SubscribeAsync(string endpoint = "https://push.example.com/a", string language = "es", Account? person = null)
        {
            (await server.As(person ?? SugaryServer.Her).PutAsJsonAsync("/api/push/subscription",
                new { endpoint, p256dh = "key", auth = "secret", language })).EnsureSuccessStatusCode();
        }

        public async Task PeriodsAsync(Account? person = null)
        {
            var client = server.As(person ?? SugaryServer.Her);
            (await client.PostAsJsonAsync("/api/periods", new { start = "2026-09-09", end = "2026-09-13" })).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/periods", new { start = "2026-10-08", end = "2026-10-12" })).EnsureSuccessStatusCode();
        }

        public async Task TurnOnAsync(bool on = true, Account? person = null) =>
            (await server.As(person ?? SugaryServer.Her).PutAsJsonAsync("/api/cycle/reminder", new { on }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        public async Task RemindAtAsync(int month, int day, int hour)
        {
            At(month, day, hour);
            await Reminders.RemindAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }

    private static async Task<Harness> StartAsync()
    {
        var push = new RecordingPushSender();
        var clock = new FakeTimeProvider();
        var sessions = new Mock<ISessionAccounts>();
        sessions.Setup(s => s.SessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string user, string _, CancellationToken _) => new ResolvedAccount(new Account("household-home", user), "Ana", "assertion"));
        var server = await SugaryServer.StartAsync(
            configure: s => s with { WebPush = SugaryPushApiTests.Keys, PenReminderHour = 9 },
            services: services => services
                .AddSingleton<IPushMessageSender>(push)
                .AddSingleton(sessions.Object)
                .AddSingleton(sp => ActivatorUtilities.CreateInstance<PeriodReminders>(sp, new HomeCalendar(clock, _madrid))));
        return new Harness(server, push, clock);
    }

    private static string Title(JsonNode payload) => payload["title"]!.GetValue<string>();

    [Fact]
    public async Task OnceTurnedOn_SheIsToldThreeDaysBefore_FromTheReminderHour_Once()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PeriodsAsync();
        await harness.TurnOnAsync();

        await harness.RemindAtAsync(11, 2, 12);
        await harness.RemindAtAsync(11, 3, 8);
        harness.Push.Sent.ShouldBeEmpty();

        await harness.RemindAtAsync(11, 3, 9);
        await harness.RemindAtAsync(11, 4, 9);

        var (endpoint, payload) = harness.Push.Sent.ShouldHaveSingleItem();
        endpoint.ShouldBe("https://push.example.com/a");
        Title(payload).ShouldBe("Tu regla podría empezar en unos 3 días");
        payload["body"]!.GetValue<string>().ShouldBe("Hacia el 6 de noviembre, según tus últimos ciclos.");
        payload["tag"]!.GetValue<string>().ShouldBe("period");
        payload["url"]!.GetValue<string>().ShouldBe("/#cycle");
    }

    [Fact]
    public async Task NeverTurnedOn_SheIsToldNothing()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PeriodsAsync();

        await harness.RemindAtAsync(11, 4, 10);

        harness.Push.Sent.ShouldBeEmpty();
        (await harness.Server.As(SugaryServer.Her).GetFromJsonAsync<JsonNode>("/api/cycle/reminder"))!["on"]!
            .GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task TurnedOff_SheIsToldNothing()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PeriodsAsync();
        await harness.TurnOnAsync();
        await harness.TurnOnAsync(false);

        await harness.RemindAtAsync(11, 4, 10);

        harness.Push.Sent.ShouldBeEmpty();
    }

    // Past the day her cycles made likeliest she is not reminded of a period she may already be on.
    [Fact]
    public async Task OnTheLikeliestDayOrAfter_NothingIsSent()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync();
        await harness.PeriodsAsync();
        await harness.TurnOnAsync();

        await harness.RemindAtAsync(11, 6, 10);
        await harness.RemindAtAsync(11, 9, 10);

        harness.Push.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReminderNobodyHeard_IsSentWhenAPhoneSubscribes()
    {
        await using var harness = await StartAsync();
        await harness.PeriodsAsync();
        await harness.TurnOnAsync();

        await harness.RemindAtAsync(11, 4, 10);
        await harness.SubscribeAsync(language: "en");
        await harness.RemindAtAsync(11, 5, 10);

        Title(harness.Push.Sent.ShouldHaveSingleItem().Payload).ShouldBe("Your period may start in about 1 day");
    }

    [Fact]
    public async Task AReminder_ReachesItsOwnersPhonesOnly_AndNobodySugaryIsOffFor()
    {
        await using var harness = await StartAsync();
        await harness.SubscribeAsync("https://push.example.com/hers");
        await harness.SubscribeAsync("https://push.example.com/his", person: SugaryServer.Him);
        await harness.PeriodsAsync();
        await harness.TurnOnAsync();
        harness.Server.SugaryOff.PersonOff(SugaryServer.Her);

        await harness.RemindAtAsync(11, 4, 10);
        harness.Push.Sent.ShouldBeEmpty();

        harness.Server.SugaryOff.PersonOff(SugaryServer.Her, off: false);
        await harness.RemindAtAsync(11, 4, 11);
        harness.Push.Sent.ShouldHaveSingleItem().Endpoint.ShouldBe("https://push.example.com/hers");
    }
}