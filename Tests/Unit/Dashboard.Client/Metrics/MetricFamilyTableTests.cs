using System.Reflection;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Metrics;

public sealed class MetricFamilyTableTests : IDisposable
{
    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricFamilyTable _families;

    public MetricFamilyTableTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _families = new MetricFamilyTable(
            new MetricsApiService(http), _tokensStore, _toolsStore, _errorsStore,
            _schedulesStore, _memoryStore, _latencyStore, _voiceStore);
    }

    public void Dispose()
    {
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
    }

    // Nothing makes the compiler check that a declared family reaches the walk that catch-up and the
    // page load do — that is the price recorded in
    // docs/adr/0007-a-metric-family-is-named-not-typed.md. A family left out of All is a page that
    // silently stops reloading, so the list is checked here against the declarations themselves
    // rather than against a copy of the seven names.
    [Fact]
    public void All_HoldsEveryDeclaredFamily()
    {
        var declared = typeof(MetricFamilyTable)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(MetricFamily<>))
            .Select(property => (MetricFamily)property.GetValue(_families)!)
            .ToList();

        declared.Count.ShouldBe(7);
        _families.All.ShouldBe(declared, ignoreOrder: true);
    }
}