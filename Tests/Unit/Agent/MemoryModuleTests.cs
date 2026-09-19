using Agent.Modules;
using Domain.Contracts;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.Agent;

public class MemoryModuleTests
{
    [Fact]
    public void AddMemory_RegistersCronValidator_RequiredByDreamingService()
    {
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection().AddMemory(config);

        using var provider = services.BuildServiceProvider();
        // CronValidator must resolve: MemoryDreamingService's ctor depends on it, and the Agent host
        // has no ValidateOnBuild, so a missing registration would only surface at StartAsync.
        provider.GetService<ICronValidator>().ShouldNotBeNull();

        // Guard the rest of the module's registration surface by descriptor presence (not resolution —
        // several of these pull external deps like Redis that this bare-config test deliberately omits),
        // so accidentally dropping the store, recall hook, or either hosted worker is caught here.
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryStore));
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryRecallHook));
        services.ShouldContain(d => d.ImplementationType == typeof(MemoryDreamingService));
        services.ShouldContain(d => d.ImplementationType == typeof(MemoryExtractionWorker));
        services.ShouldContain(d => d.ServiceType == typeof(MemoryJudge));
    }

    [Fact]
    public void AddMemory_BindsTheJudgmentBarsFromTheMemoryConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:Judgments:Enabled"] = "false",
                ["Memory:Judgments:Gate:SkipAtOrBelow"] = "0.2",
                ["Memory:Judgments:Verify:Durable"] = "0.6",
                ["Memory:Judgments:Pairs:MaxClusterMemories"] = "8"
            })
            .Build();

        using var provider = new ServiceCollection().AddMemory(config).BuildServiceProvider();
        var settings = provider.GetRequiredService<MemoryJudgmentSettings>();

        settings.Enabled.ShouldBeFalse();
        settings.Gate.SkipAtOrBelow.ShouldBe(0.2);
        settings.Verify.Durable.ShouldBe(0.6);
        settings.Verify.Supported.ShouldBe(0.5);
        settings.Pairs.MaxClusterMemories.ShouldBe(8);
    }

    [Fact]
    public void AddMemory_TakesTheEmbeddingAddressAndModelFromTheMemoryConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:Embedding:BaseAddress"] = "http://embeddings.invalid/v1/",
                ["Memory:Embedding:Model"] = "some-embedding-model",
                ["openRouter:apiUrl"] = "https://hosted.invalid/api/v1/"
            })
            .Build();

        using var provider = new ServiceCollection().AddMemory(config).BuildServiceProvider();
        var options = provider.GetRequiredService<EmbeddingOptions>();

        options.BaseAddress.ShouldBe("http://embeddings.invalid/v1/");
        options.Model.ShouldBe("some-embedding-model");
    }

    [Fact]
    public void AddMemory_DefaultsToTheLocalServerWithNoKeyAndItsOwnDimension()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["openRouter:apiKey"] = "sk-hosted"
            })
            .Build();

        using var provider = new ServiceCollection().AddMemory(config).BuildServiceProvider();
        var options = provider.GetRequiredService<EmbeddingOptions>();

        options.BaseAddress.ShouldBe("http://lemonade:13305/api/v1/");
        options.Dimension.ShouldBe(1024);
        // The local server has no key to check, and the hosted provider's must not leak onto
        // the Docker network.
        options.ApiKey.ShouldBeNull();
    }
}