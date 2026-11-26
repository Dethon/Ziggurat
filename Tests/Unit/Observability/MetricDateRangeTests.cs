using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Observability;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability;

public class MetricDateRangeTests
{
    private static readonly DateOnly _today = new(2026, 3, 24);

    private static async Task<MetricDateRange?> BindAsync(string queryString, TimeOnly? timeOfDay = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(_today, timeOfDay ?? TimeOnly.MinValue, TimeSpan.Zero));
        var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString(queryString);

        return await MetricDateRange.BindAsync(context);
    }

    [Fact]
    public async Task BindAsync_NoValues_DefaultsBothToTheTimeProviderToday()
    {
        var range = await BindAsync("");

        range.ShouldBe(MetricDateRange.Spanning(_today, _today));
    }

    [Fact]
    public async Task BindAsync_OnlyFrom_DefaultsToToTheTimeProviderToday()
    {
        var range = await BindAsync("?from=2026-03-01");

        range.ShouldBe(MetricDateRange.Spanning(new DateOnly(2026, 3, 1), _today));
    }

    [Fact]
    public async Task BindAsync_OnlyTo_DefaultsFromToTheTimeProviderToday()
    {
        var range = await BindAsync("?to=2026-03-31");

        range.ShouldBe(MetricDateRange.Spanning(_today, new DateOnly(2026, 3, 31)));
    }

    // A day asked for by date is the whole UTC day: midnight in, the next midnight out.
    [Fact]
    public async Task BindAsync_Dates_CoverTheWholeOfEachDay()
    {
        var range = (await BindAsync("?from=2026-03-01&to=2026-03-02"))!;

        range.Contains(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        range.Contains(new DateTimeOffset(2026, 3, 2, 23, 59, 59, TimeSpan.Zero)).ShouldBeTrue();
        range.Contains(new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
        range.Dates.ShouldBe([new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2)]);
    }

    // The last day is the last twenty-four hours whenever it is asked: a calendar "today" covered a
    // few minutes just after midnight and nearly a whole day just before the next.
    [Theory]
    [InlineData(0, 5)]
    [InlineData(7, 30)]
    [InlineData(23, 55)]
    public async Task BindAsync_Days_IsThatManyDaysBackFromNow_WhateverTheTimeOfDay(int hour, int minute)
    {
        var now = new DateTimeOffset(_today, new TimeOnly(hour, minute), TimeSpan.Zero);

        var range = (await BindAsync("?days=1", new TimeOnly(hour, minute)))!;

        range.From.ShouldBe(now.AddHours(-24));
        range.Contains(now.AddHours(-23)).ShouldBeTrue();
        range.Contains(now.AddHours(-25)).ShouldBeFalse();
        range.Contains(now).ShouldBeTrue();
        range.Dates.ShouldBe([_today.AddDays(-1), _today]);
    }

    [Fact]
    public async Task BindAsync_Days_TakesPrecedenceOverDates()
    {
        var range = await BindAsync("?days=7&from=2026-01-01", new TimeOnly(12, 0));

        range.ShouldBe(MetricDateRange.Last(7, new DateTimeOffset(_today, new TimeOnly(12, 0), TimeSpan.Zero)));
    }

    // Null is how a minimal API parameter says "bad request": the framework turns it into the same
    // 400 the two nullable DateOnly parameters returned for input that would not parse.
    [Theory]
    [InlineData("?from=yesterday")]
    [InlineData("?to=32nd")]
    [InlineData("?from=2026-03-01&to=")]
    [InlineData("?days=0")]
    [InlineData("?days=-1")]
    [InlineData("?days=week")]
    public async Task BindAsync_UnparseableValue_ReturnsNull(string queryString)
    {
        var range = await BindAsync(queryString);

        range.ShouldBeNull();
    }

    // Building the endpoints is the assertion: a parameter minimal APIs cannot bind fails here,
    // and every metrics route now takes the range as one.
    [Fact]
    public void MapMetricsApi_EveryRoute_BindsItsParameters()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        builder.Services.AddSingleton<MetricsQueryService>();
        var app = builder.Build();

        app.MapMetricsApi();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .ToList();
        endpoints.Count.ShouldBe(33);
        endpoints.ShouldAllBe(e => e is RouteEndpoint);
    }
}