using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// A built app reads on every page it shows, so the read registry it reads through is kept per household,
// agent and viewer while it is used, and built again only when the household changed the agent or its
// servers since, or nobody read through it for a while — against servers in process, over the wire.
public sealed class ReadRegistryCacheTests
{
    private const string Household = "0123456789abcdef0123456789abcdef";

    private static readonly IdentityClaims _fran = IdentityClaims.Person("fran", Household, AppChannels.Apps, ["member"]);
    private static readonly IdentityClaims _alex = IdentityClaims.Person("alex", Household, AppChannels.Apps, ["member"]);

    [Fact]
    public async Task ReadsOfOneViewerAndAgent_ShareOneRegistry_AndAnotherViewerHasTheirOwn()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "a" });
        var households = new AgentHouseholds();
        households.Add(Household, Jonas(notes.Endpoint));
        await using var cache = Cache(households, new Dictionary<string, string>(), new FakeTimeProvider());

        await using var first = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None);
        await using var second = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None);
        await using var another = await cache.LeaseAsync(_alex, "jonas", CancellationToken.None);

        second.Registry.ShouldBeSameAs(first.Registry);
        another.Registry.ShouldNotBeSameAs(first.Registry);
    }

    // A household server going offline or coming back makes the kept registry stale, as it makes a turn's
    // session: the next read is built again and reaches what is there.
    [Fact]
    public async Task TheHouseholdsServersChanging_RebuildsTheRegistry()
    {
        await using var vault = await TextFileServer.StartAsync("vault", new Dictionary<string, string> { ["a.md"] = "a" });
        var households = new AgentHouseholds();
        households.Add(Household, [Jonas("vault")], null, ownServers: ["vault"]);
        var online = new Dictionary<string, string> { ["vault"] = vault.Endpoint };
        await using var cache = Cache(households, online, new FakeTimeProvider());

        ReadRegistry connected;
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            connected = lease.Registry;
            Mounts(connected).ShouldBe(["/vault"]);
        }

        online.Remove("vault");
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            lease.Registry.ShouldNotBeSameAs(connected);
            Mounts(lease.Registry).ShouldBeEmpty();
            lease.Registry.Offline.ShouldBe(["vault"]);
        }

        online["vault"] = vault.Endpoint;
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            Mounts(lease.Registry).ShouldBe(["/vault"]);
        }
    }

    // A registry rebuilt while a read still holds the old one lets that read finish on it: it is let go
    // only once nothing holds it.
    [Fact]
    public async Task ARegistryReplacedWhileARead_HoldsIt_ServesThatReadToItsEnd()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "kept" });
        var households = new AgentHouseholds();
        households.Add(Household, Jonas(notes.Endpoint));
        await using var cache = Cache(households, new Dictionary<string, string>(), new FakeTimeProvider());

        var held = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None);
        households.Add(Household, Jonas(notes.Endpoint) with { Description = "Rewritten by an owner." });
        await using (var fresh = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            fresh.Registry.ShouldNotBeSameAs(held.Registry);
        }

        (await ReadAsync(held.Registry, "/notes/a.md")).ShouldContain("kept");
        await held.DisposeAsync();
    }

    // Nobody reading through a registry for the idle span lets it go; the next read builds again.
    [Fact]
    public async Task ARegistryNobodyReadThroughForTheIdleSpan_IsBuiltAgain()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "a" });
        var households = new AgentHouseholds();
        households.Add(Household, Jonas(notes.Endpoint));
        var clock = new FakeTimeProvider();
        await using var cache = Cache(households, new Dictionary<string, string>(), clock);

        ReadRegistry first;
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            first = lease.Registry;
        }

        clock.Advance(new AppReadSettings().RegistryIdle - TimeSpan.FromSeconds(1));
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            lease.Registry.ShouldBeSameAs(first);
        }

        clock.Advance(new AppReadSettings().RegistryIdle);
        await using (var lease = await cache.LeaseAsync(_fran, "jonas", CancellationToken.None))
        {
            lease.Registry.ShouldNotBeSameAs(first);
            (await ReadAsync(lease.Registry, "/notes/a.md")).ShouldContain("a");
        }
    }

    private static async Task<string> ReadAsync(ReadRegistry registry, string path)
    {
        var resolution = registry.Registry.Resolve(path).ShouldBeOfType<FsResult<FileSystemResolution>.Ok>().Value.As(registry.Caller);
        var read = await resolution.Backend.ReadAsync(resolution.RelativePath, null, null, CancellationToken.None);
        return read.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
    }

    private static IReadOnlyList<string> Mounts(ReadRegistry read) =>
        [.. read.Registry.GetMounts().Select(m => m.MountPoint).Order()];

    private static AgentDefinition Jonas(params string[] endpoints) => new()
    {
        Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = endpoints, EnabledFeatures = ["filesystem"]
    };

    private static ReadRegistryCache Cache(
        AgentHouseholds households, IReadOnlyDictionary<string, string> online, TimeProvider clock)
    {
        var definitions = new AgentDefinitionProvider(new HouseholdAgentSource(households, new SharedServers()), new CustomAgentRegistry());
        return new ReadRegistryCache(
            new AgentReadRegistries(definitions, McpTestSecret.Credentials, clock, new InProcessOwnServers(online), McpTestSecret.CallTokens),
            clock, new AppReadSettings());
    }
}