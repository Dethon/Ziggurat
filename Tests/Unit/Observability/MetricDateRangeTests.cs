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

    private static async Task<MetricDateRange?> BindAsync(string queryString)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(_today, TimeOnly.MinValue, TimeSpan.Zero));
        var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString(queryString);

        return await MetricDateRange.BindAsync(context);
    }

    [Fact]
    public async Task BindAsync_NoValues_DefaultsBothToTheTimeProviderToday()
    {
        var range = await BindAsync("");

        range.ShouldBe(new MetricDateRange(_today, _today));
    }

    [Fact]
    public async Task BindAsync_OnlyFrom_DefaultsToToTheTimeProviderToday()
    {
        var range = await BindAsync("?from=2026-03-01");

        range.ShouldBe(new MetricDateRange(new DateOnly(2026, 3, 1), _today));
    }

    [Fact]
    public async Task BindAsync_OnlyTo_DefaultsFromToTheTimeProviderToday()
    {
        var range = await BindAsync("?to=2026-03-31");

        range.ShouldBe(new MetricDateRange(_today, new DateOnly(2026, 3, 31)));
    }

    // Null is how a minimal API parameter says "bad request": the framework turns it into the same
    // 400 the two nullable DateOnly parameters returned for input that would not parse.
    [Theory]
    [InlineData("?from=yesterday")]
    [InlineData("?to=32nd")]
    [InlineData("?from=2026-03-01&to=")]
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
        endpoints.Count.ShouldBe(22);
        endpoints.ShouldAllBe(e => e is RouteEndpoint);
    }
}