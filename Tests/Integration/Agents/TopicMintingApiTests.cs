using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.WebChat;
using Domain.Security;
using global::Agent.App;
using Infrastructure.Clients.Topics;
using Infrastructure.Conversations;
using Infrastructure.StateManagers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// Voice asks the agent for the topic a conversation opens under, carrying the accounts service's
// assertion of who spoke, and the agent writes it into the topic store the chat client reads. Asked
// as voice asks it — through the client voice holds, over HTTP, to the route as the agent maps it —
// and read back as the chat client reads it, through the store's viewer-filtered page: whose a topic
// is, which household sees it and that nobody else does are one answer across the three.
[Trait("Category", "Integration")]
public sealed class TopicMintingApiTests(TopicSearchFixture redis) : IClassFixture<TopicSearchFixture>, IAsyncLifetime
{
    private const string Home = "home";
    private static readonly Account Alice = new(Home, "alice");

    // Every test its own agent, so the store the class shares never answers one test with another's.
    private readonly string _agent = $"agent-topics-{Guid.NewGuid():N}";
    private readonly List<ErrorEvent> _errors = [];
    private RedisThreadStateStore _store = null!;
    private WebApplication _app = null!;
    private AgentTopicsClient _client = null!;

    public async Task InitializeAsync()
    {
        _store = new RedisThreadStateStore(
            redis.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromDays(365) }, TimeProvider.System);
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => _errors.AddRange(e is ErrorEvent error ? [error] : []));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddSingleton<IConversationFactory>(new ConversationFactory(_store, TimeProvider.System))
            .AddSingleton(TestAssertions.Verifier())
            .AddSingleton(metrics.Object);
        _app = builder.Build();
        _app.MapTopicMinting();
        await _app.StartAsync();
        _client = new AgentTopicsClient(_app.GetTestClient());
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task ARecognisedSpeakersConversation_IsTheirPrivateTopic()
    {
        var minted = await _client.MintAsync(
            new TopicMintRequest(_agent, "Kitchen @ Cocina", "Alice"), TestAssertions.Of(Alice, ChannelIds.Voice), default);

        var topic = (await PageAsync(new TopicViewer("alice", Home))).ShouldHaveSingleItem();
        (topic.TopicId, topic.Name, topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName)
            .ShouldBe((minted.TopicId, "Kitchen @ Cocina", "alice", Home, false, "Alice"));
        minted.ConversationId.ShouldBe($"{topic.ChatId}:{topic.ThreadId}");
        (await PageAsync(new TopicViewer("bob", Home))).ShouldBeEmpty();
    }

    // The guest is somebody in the house acting as no person: a topic nobody owns, recording the
    // household whose guest they are, which everyone of it sees and no other household does.
    [Fact]
    public async Task TheGuestsConversation_IsATopicNobodyOwns_OfTheGuestsHousehold()
    {
        await _client.MintAsync(
            new TopicMintRequest(_agent, "Kitchen @ Cocina", "Kitchen"), TestAssertions.OfGuest(Home, ChannelIds.Voice), default);

        var topic = (await PageAsync(new TopicViewer("bob", Home))).ShouldHaveSingleItem();
        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName)
            .ShouldBe(((string?)null, Home, true, (string?)null));
        (await PageAsync(new TopicViewer("laura", "elsewhere"))).ShouldBeEmpty();
    }

    // Whose a topic is was signed; nothing a caller writes beside the assertion moves it.
    [Fact]
    public async Task ABodyNamingSomebodyElse_StillMintsForTheAssertedPerson()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TopicMinting.Path)
        {
            Content = JsonContent.Create(new
            {
                agentId = _agent,
                topicName = "Kitchen @ Cocina",
                sender = "Bob",
                principal = new { channelId = "voice", userId = "bob", account = new { tenantId = Home, userId = "bob" } }
            })
        };
        request.Headers.Add(ForwardedAccount.AssertionHeader, TestAssertions.Of(Alice, ChannelIds.Voice));
        request.Headers.Add(ForwardedAccount.UserHeader, "bob");

        using var response = await _app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PageAsync(new TopicViewer("alice", Home))).ShouldHaveSingleItem().OwnerId.ShouldBe("alice");
        (await PageAsync(new TopicViewer("bob", Home))).ShouldBeEmpty();
    }

    // Without an assertion the accounts service signed for a voice turn — none, one signed by a key
    // it does not publish, one signed for the chat client, one naming nobody — nothing is minted,
    // and the refusal is on the errors feed as a turn's refusal is.
    public static TheoryData<string, string?> Unbelieved => new()
    {
        { "none", null },
        { "forged", TestAssertions.Forged(Alice, ChannelIds.Voice) },
        { "for the chat client", TestAssertions.Of(Alice, ChannelIds.SignalR) },
        { "naming nobody", TestAssertions.OfNobody(ChannelIds.Voice) }
    };

    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task AMintWithoutABelievableAssertion_IsUnauthorizedAndMintsNothing(string _, string? assertion)
    {
        var refused = await Should.ThrowAsync<HttpRequestException>(() =>
            _client.MintAsync(new TopicMintRequest(_agent, "Kitchen @ Cocina", "Alice"), assertion, default));

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PageAsync(new TopicViewer("alice", Home, SeesAll: true))).ShouldBeEmpty();
        _errors.ShouldHaveSingleItem().ErrorType.ShouldBe(TopicMintingApi.RefusedErrorType);
    }

    private async Task<IReadOnlyList<TopicMetadata>> PageAsync(TopicViewer viewer) =>
        (await _store.GetTopicPageAsync(_agent, "default", viewer, cursor: null, pageSize: 20)).Topics;
}