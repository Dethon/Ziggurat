using System.Text;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.Agent;

// Sort is an enum so a typo fails at bind time instead of shipping an unroutable value to
// OpenRouter. These pin that the binder actually behaves that way -- nothing else would catch
// it, because a bad sort would otherwise only surface as a silently ignored request field.
public class ProviderRoutingBindingTests
{
    [Theory]
    [InlineData("price", ProviderSort.Price)]
    [InlineData("throughput", ProviderSort.Throughput)]
    [InlineData("latency", ProviderSort.Latency)]
    [InlineData("Throughput", ProviderSort.Throughput)]
    public void Bind_ValidSort_MapsToMember(string configured, ProviderSort expected)
    {
        Bind(("providerRouting:sort", configured)).Sort.ShouldBe(expected);
    }

    [Fact]
    public void Bind_InvalidSort_ThrowsNamingThePath()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => Bind(("providerRouting:sort", "cheapest")));

        ex.Message.ShouldContain("providerRouting:sort");
    }

    // Enum.Parse accepts numeric strings including undefined values, so without a guard
    // "sort": 7 binds to (ProviderSort)7 and reaches the wire as "7" -- the exact silent
    // misconfiguration the enum exists to prevent. Alphabetic typos alone failing loudly
    // is not enough.
    // The binder reaches the property through reflection, so the guard surfaces wrapped
    // (today in TargetInvocationException); the wrapper type is implementation detail, the
    // ArgumentOutOfRangeException at the root of the chain is the contract.
    [Fact]
    public void Bind_UndefinedNumericSort_Throws()
    {
        var ex = Should.Throw<Exception>(() => Bind(("providerRouting:sort", "7")));

        ex.GetBaseException().ShouldBeOfType<ArgumentOutOfRangeException>()
            .Message.ShouldContain(nameof(ProviderSort));
    }

    [Fact]
    public void Bind_ArraysAndFlags_MapFromIndexedKeys()
    {
        var routing = Bind(
            ("providerRouting:order:0", "deepinfra"),
            ("providerRouting:order:1", "novita"),
            ("providerRouting:only:0", "deepinfra"),
            ("providerRouting:ignore:0", "chutes"),
            ("providerRouting:allowFallbacks", "false"));

        routing.Order.ShouldBe(["deepinfra", "novita"]);
        routing.Only.ShouldBe(["deepinfra"]);
        routing.Ignore.ShouldBe(["chutes"]);
        routing.AllowFallbacks.ShouldBe(false);
    }

    // OpenRouter documents a bare number as shorthand for the p50 cutoff, so config may spell a
    // threshold either way. Binding the scalar form needs a TypeConverter -- without one the
    // binder sees a value on a complex-typed key and throws.
    [Fact]
    public void Bind_ScalarThreshold_MapsToP50()
    {
        var routing = Bind(("providerRouting:preferredMinThroughput", "80"));

        routing.PreferredMinThroughput.ShouldNotBeNull();
        routing.PreferredMinThroughput!.P50.ShouldBe(80);
        routing.PreferredMinThroughput.P90.ShouldBeNull();
    }

    [Fact]
    public void Bind_PercentileThreshold_MapsEachCutoff()
    {
        var routing = Bind(
            ("providerRouting:preferredMaxLatency:p50", "1"),
            ("providerRouting:preferredMaxLatency:p90", "3"),
            ("providerRouting:preferredMaxLatency:p99", "5"));

        routing.PreferredMaxLatency.ShouldNotBeNull();
        routing.PreferredMaxLatency!.P50.ShouldBe(1);
        routing.PreferredMaxLatency.P75.ShouldBeNull();
        routing.PreferredMaxLatency.P90.ShouldBe(3);
        routing.PreferredMaxLatency.P99.ShouldBe(5);
    }

    [Fact]
    public void Bind_MaxPrice_MapsEachCeiling()
    {
        var routing = Bind(
            ("providerRouting:maxPrice:prompt", "1"),
            ("providerRouting:maxPrice:completion", "2.5"),
            ("providerRouting:maxPrice:request", "0.01"),
            ("providerRouting:maxPrice:image", "0.5"));

        routing.MaxPrice.ShouldNotBeNull();
        routing.MaxPrice!.Prompt.ShouldBe(1);
        routing.MaxPrice.Completion.ShouldBe(2.5);
        routing.MaxPrice.Request.ShouldBe(0.01);
        routing.MaxPrice.Image.ShouldBe(0.5);
    }

    // A negative threshold is silent: nothing is deprioritized and no provider is excluded, so
    // the misconfiguration only ever shows up as routing that ignores the preference entirely.
    [Fact]
    public void Bind_NegativeThreshold_Throws()
    {
        var ex = Should.Throw<Exception>(() => Bind(("providerRouting:preferredMinThroughput", "-80")));

        ex.GetBaseException().ShouldBeOfType<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Construct_NegativeThreshold_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderThreshold { P90 = -1 });
    }

    [Fact]
    public void Construct_NegativeMaxPrice_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderMaxPrice { Prompt = -1 });
    }

    [Fact]
    public void Bind_NonNumericThreshold_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Bind(("providerRouting:preferredMaxLatency", "fast")));
    }

    [Fact]
    public void Bind_MissingSection_YieldsNull()
    {
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()
            .ShouldBeNull();
    }

    // The JSON provider records an empty object as a null-valued key, so `"providerRouting": {}`
    // binds to null and the agent INHERITS the global default -- {} is not a wholesale opt-out.
    // CLAUDE.md documents that trap; this pins the binder behavior the documentation relies on.
    // The key-presence assert keeps the null from ever meaning "the test's JSON lost the key".
    [Fact]
    public void Bind_EmptyJsonObject_YieldsNull()
    {
        var config = BuildJson("""{"providerRouting": {}}""");

        config.GetChildren().Select(c => c.Key).ShouldContain("providerRouting");
        config.GetSection("providerRouting").Get<ProviderRouting>().ShouldBeNull();
    }

    // The working opt-out spelling for a future non-empty global default:
    // {"allowFallbacks": true} binds to a real instance -- so it shadows the global wholesale --
    // whose only wire effect is `allow_fallbacks: true`, OpenRouter's default, leaving the
    // agent on balanced routing.
    [Fact]
    public void Bind_AllowFallbacksOnly_YieldsAnInstanceThatShadowsButStaysBalanced()
    {
        var routing = BuildJson("""{"providerRouting": {"allowFallbacks": true}}""")
            .GetSection("providerRouting")
            .Get<ProviderRouting>();

        routing.ShouldNotBeNull();
        routing.IsEmpty.ShouldBeFalse();
        routing.Sort.ShouldBeNull();
        routing.Order.ShouldBeNull();
    }

    [Fact]
    public void IsEmpty_NoFieldsSet_IsTrue()
    {
        new ProviderRouting().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_EmptyArrays_IsTrue()
    {
        new ProviderRouting { Order = [], Only = [], Ignore = [] }.IsEmpty.ShouldBeTrue();
    }

    // `"maxPrice": {"foo": 1}` binds to an instance with every cutoff unset. Treating that as a
    // field being set would put an empty `provider` object on the wire, which is the one shape
    // balanced routing must never take.
    [Fact]
    public void IsEmpty_CutoffLessThresholdObjects_IsTrue()
    {
        new ProviderRouting
        {
            PreferredMinThroughput = new ProviderThreshold(),
            PreferredMaxLatency = new ProviderThreshold(),
            MaxPrice = new ProviderMaxPrice()
        }.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NonEmptyRoutings))]
    public void IsEmpty_AnyFieldSet_IsFalse(string _, ProviderRouting routing)
    {
        routing.IsEmpty.ShouldBeFalse();
    }

    public static IEnumerable<object[]> NonEmptyRoutings =>
    [
        ["sort", new ProviderRouting { Sort = ProviderSort.Price }],
        ["order", new ProviderRouting { Order = ["deepinfra"] }],
        ["only", new ProviderRouting { Only = ["deepinfra"] }],
        ["ignore", new ProviderRouting { Ignore = ["chutes"] }],
        ["allowFallbacks", new ProviderRouting { AllowFallbacks = false }],
        ["preferredMinThroughput", new ProviderRouting
            { PreferredMinThroughput = new ProviderThreshold { P50 = 80 } }],
        ["preferredMaxLatency", new ProviderRouting
            { PreferredMaxLatency = new ProviderThreshold { P90 = 3 } }],
        ["maxPrice", new ProviderRouting { MaxPrice = new ProviderMaxPrice { Prompt = 1 } }]
    ];

    private static ProviderRouting Bind(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()!;

    private static IConfigurationRoot BuildJson(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
}