using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Infrastructure.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Integration.McpServerTests;

// web_search asks Brave on the calling household's own key, read through what the call proved: the
// server holds no key, so a household that set none searches nothing rather than on somebody else's.
public class McpWebSearchHouseholdKeyTests
{
    private readonly RecordingBrave _brave = new();

    private Task<WebIdentityServerHarness> StartAsync(params (string Household, string Key)[] keys) =>
        WebIdentityServerHarness.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(new EveryHouseholdIn(
                TimeZoneInfo.Utc,
                braveKeys: keys.ToDictionary(k => k.Household, k => new BraveSearchKey(k.Key)))));
            services.AddHttpClient(BraveSearchClients.ClientName).ConfigurePrimaryHttpMessageHandler(() => _brave);
        });

    private static ConversationContext Of(string household) =>
        WebIdentityServerHarness.Context("ana", new Account(household, "ana"));

    private static Dictionary<string, object?> Search(string query) => new() { ["query"] = query };

    [Fact]
    public async Task ASearch_AsksBraveWithTheCallingHouseholdsOwnKey()
    {
        await using var server = await StartAsync(("casa", "key-of-casa"), ("otra", "key-of-otra"));

        (await server.CallAsync("web_search", Search("tiempo"), Of("casa"))).IsError.ShouldNotBe(true);
        (await server.CallAsync("web_search", Search("tiempo"), Of("otra"))).IsError.ShouldNotBe(true);

        _brave.Tokens.ShouldBe(["key-of-casa", "key-of-otra"]);
    }

    [Fact]
    public async Task AHouseholdWithNoKey_IsToldAnOwnerAddsOne_AndBraveIsNotAsked()
    {
        await using var server = await StartAsync(("otra", "key-of-otra"));

        var refused = await server.CallAsync("web_search", Search("tiempo"), Of("casa"));

        var text = string.Concat(refused.Content.OfType<TextContentBlock>().Select(t => t.Text));
        text.ShouldContain("\"errorCode\":\"authentication\"");
        text.ShouldContain("no Brave Search key");
        _brave.Tokens.ShouldBeEmpty();
    }

    // A noop search asks nothing of Brave, so it needs no key either.
    [Fact]
    public async Task ANoopSearch_IsAnsweredForAHouseholdWithNoKey()
    {
        await using var server = await StartAsync();

        var answered = await server.CallAsync("web_search", Search("noop"), Of("casa"));

        string.Concat(answered.Content.OfType<TextContentBlock>().Select(t => t.Text)).ShouldContain("noop");
        answered.IsError.ShouldNotBe(true);
        _brave.Tokens.ShouldBeEmpty();
    }

    private sealed class RecordingBrave : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _tokens = new();

        public IReadOnlyList<string> Tokens => [.. _tokens];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _tokens.Enqueue(request.Headers.GetValues("X-Subscription-Token").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"web":{"results":[]}}""", Encoding.UTF8, "application/json")
            });
        }
    }
}