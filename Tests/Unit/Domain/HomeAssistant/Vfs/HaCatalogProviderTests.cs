using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogProviderTests
{
    [Fact]
    public async Task GetAsync_BuildsCatalogFromClient()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.kitchen"]}]}"""
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(1);
        catalog.Services.Count.ShouldBe(1);
        catalog.Areas.ShouldContain(a => a.Id == "salon" && a.EntityIds.Contains("light.kitchen"));
    }

    [Fact]
    public async Task GetAsync_SuccessfulButEmpty_CachesForFullTtl()
    {
        var client = new CountingClient(); // no states, but the call succeeds (not a failure)
        var time = new FakeTimeProvider();
        var provider = new HaCatalogProvider(() => client, time);

        await provider.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(60)); // past the 30s failure TTL
        await provider.GetAsync(CancellationToken.None);

        client.StateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_AfterFailure_RepollsOnceFailureTtlElapses()
    {
        var client = new FlakyClient { States = { Entity("light.kitchen", "off") } };
        var time = new FakeTimeProvider();
        var provider = new HaCatalogProvider(() => client, time);

        (await provider.GetAsync(CancellationToken.None)).Entities.ShouldBeEmpty();
        client.StateCalls.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(15)); // within the failure TTL — still cached
        await provider.GetAsync(CancellationToken.None);
        client.StateCalls.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(30)); // past the failure TTL — re-polls, now recovered
        client.Throw = false;
        (await provider.GetAsync(CancellationToken.None)).Entities.Count.ShouldBe(1);
        client.StateCalls.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_Cancelled_PropagatesAndDoesNotPoisonCache()
    {
        var client = new CancellingClient { States = { Entity("light.kitchen", "off") } };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        // Cancellation must propagate, not be swallowed into an empty catalog cached for the failure TTL.
        await Should.ThrowAsync<OperationCanceledException>(() => provider.GetAsync(CancellationToken.None));

        // Cache wasn't poisoned: the next call rebuilds and yields the real catalog (no blind window).
        (await provider.GetAsync(CancellationToken.None)).Entities.Count.ShouldBe(1);
        client.StateCalls.ShouldBe(2);
    }

    private sealed class CountingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return base.ListStatesAsync(ct);
        }
    }

    private sealed class FlakyClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public bool Throw { get; set; } = true;

        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return Throw ? throw new InvalidOperationException("HA down") : base.ListStatesAsync(ct);
        }
    }

    private sealed class CancellingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        private bool _cancel = true;

        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            if (!_cancel)
            {
                return base.ListStatesAsync(ct);
            }
            _cancel = false;
            throw new OperationCanceledException();
        }
    }
}