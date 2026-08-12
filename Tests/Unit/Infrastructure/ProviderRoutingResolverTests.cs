using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class ProviderRoutingResolverTests
{
    private const string Model = "z-ai/glm-5.2";

    private readonly CapturingLoggerProvider _logProvider = new(LogLevel.Warning);
    private readonly ILogger _logger;

    public ProviderRoutingResolverTests() =>
        _logger = LoggerFactory.Create(b => b.AddProvider(_logProvider)).CreateLogger("routing");

    private IReadOnlyCollection<string> Logs => _logProvider.Messages;

    // The global default carries an `ignore` the declared routing never sets: a field-by-field
    // merge would leak it while leaving `sort` intact, so only a fixture shaped like this can
    // tell wholesale replacement from a merge.
    [Fact]
    public void Resolve_RoutingIsDeclared_UsesItWholesaleAndNotTheGlobalDefault()
    {
        var declared = new ProviderRouting { Sort = ProviderSort.Latency };

        var resolved = ProviderRoutingResolver.Resolve(
            declared,
            new ProviderRouting { Sort = ProviderSort.Throughput, Ignore = ["chutes"] },
            Model,
            "routed",
            _logger);

        resolved.ShouldBe(declared);
        resolved!.Ignore.ShouldBeNull();
    }

    [Fact]
    public void Resolve_NoRoutingIsDeclared_InheritsTheGlobalDefault()
    {
        var globalRouting = new ProviderRouting { Sort = ProviderSort.Throughput };

        var resolved = ProviderRoutingResolver.Resolve(null, globalRouting, Model, "plain", _logger);

        resolved.ShouldBe(globalRouting);
    }

    // Balanced routing is the absence of a provider object, so "neither set" must resolve to
    // null rather than to some empty-but-present default.
    [Fact]
    public void Resolve_NeitherDeclaredNorGlobal_ResolvesToNull()
    {
        ProviderRoutingResolver.Resolve(null, null, Model, "plain", _logger).ShouldBeNull();
    }

    [Fact]
    public void Resolve_RoutingTripsAnAdvisory_LogsAWarningNamingTheIdentity()
    {
        var routing = new ProviderRouting { Order = ["deepinfra"] };

        ProviderRoutingResolver.Resolve(routing, null, Model, "noisy", _logger);

        Logs.ShouldContain(m => m.Contains("noisy") && m.Contains("sticky routing"));
    }

    // Advisories run on the resolved routing, not the declared one: a global default that trips
    // one must warn for every agent that inherits it, or the config mistake stays invisible
    // exactly where it does the most damage.
    [Fact]
    public void Resolve_InheritedGlobalRoutingTripsAnAdvisory_LogsAWarningNamingTheIdentity()
    {
        var globalRouting = new ProviderRouting { Order = ["deepinfra"] };

        ProviderRoutingResolver.Resolve(null, globalRouting, Model, "plain", _logger);

        Logs.ShouldContain(m => m.Contains("plain") && m.Contains("sticky routing"));
    }

    // Asserts the absence of an advisory rather than an empty log: nothing else may become a
    // tripwire for unrelated warnings on this logger.
    [Fact]
    public void Resolve_RoutingIsClean_LogsNoAdvisory()
    {
        var routing = new ProviderRouting { Sort = ProviderSort.Latency };

        ProviderRoutingResolver.Resolve(routing, null, Model, "quiet", _logger);

        Logs.ShouldNotContain(m => m.Contains("sticky routing") || m.Contains("providerRouting.sort"));
    }

    [Fact]
    public void Resolve_NoLogger_StillResolves()
    {
        var routing = new ProviderRouting { Order = ["deepinfra"] };

        ProviderRoutingResolver.Resolve(routing, null, Model, "silent", null).ShouldBe(routing);
    }
}