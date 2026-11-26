using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using McpServerApps.Asks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A built app's page asks its assistant at /_ask on its own origin. The apps server settles the viewer
// and the app as for a read, sends the ask to the app's building agent as a turn on its own channel, and
// waits for the whole reply. The agent is played over MCP by its own channel connection, so everything
// asserted here crossed the wire both ways: the message the agent is sent, the reply as chunks, and the
// HTTP answer the page reads.
public sealed class AppsAsksTests : IAsyncLifetime
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private const string Published = "Answer in one word, as the published app asks.";
    private const string Tree = "Answer in two words, as the app being edited asks.";

    private AppsServer _apps = null!;
    private McpChannelConnection _agent = null!;
    private string _id = null!;

    public async Task InitializeAsync()
    {
        _apps = await AppsServer.StartAsync();
        _agent = await _apps.AgentAsync();
        _id = await InstructedAppAsync(_apps);
    }

    public async Task DisposeAsync() => await _apps.DisposeAsync();

    [Fact]
    public async Task AnAsk_IsATurnOfTheBuildingAgent_ForTheViewer_AnsweredByTheLastMessagesText()
    {
        var host = _apps.Published("glucose", _id);
        var session = _apps.SessionFor(AppsServer.Her, host);

        var answer = _apps.AskAsync(host, """{"text":"How was my morning?"}""", session);
        var turn = await NextTurnAsync(_agent);

        turn.AgentId.ShouldBe("jonas");
        turn.Content.ShouldBe("How was my morning?");
        // What the agent believes: the session's own assertion. The principal is a label beside it.
        turn.Assertion.ShouldBe(AssertionOf(session, host));
        turn.Principal.ShouldBe(new Principal(AppChannels.Apps, AppsServer.Her.UserId, AppsServer.Her));
        turn.AppTurn.ShouldNotBeNull();
        turn.AppTurn.AppId.ShouldBe(_id);
        turn.AppTurn.Instructions.ShouldBe(Published);
        turn.AppTurn.Servers.ShouldBeEmpty();
        turn.TurnKey.ShouldNotBeNullOrWhiteSpace();
        ConversationIdentity.Parse(turn.ConversationId).ShouldNotBeNull();
        // No topic is asked for: an app's asks stay out of WebChat.
        turn.ReviewCopy.ShouldBeNull();
        // Neither a shape nor a model asked for: the agent's own, answering in text.
        turn.ResponseFormat.ShouldBeNull();
        turn.ConfigPatch.ShouldBeNull();

        await SendAsync(turn, ReplyContentType.Text, "Let me look.", "resp_1");
        await SendAsync(turn, ReplyContentType.Text, "Quiet", "resp_2");
        await SendAsync(turn, ReplyContentType.Text, "ly.", "resp_2");
        await SendAsync(turn, ReplyContentType.StreamComplete, "", "resp_2", isComplete: true);

        using var response = await answer;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body["text"]!.GetValue<string>().ShouldBe("Quietly.");
        body["conversation"]!.GetValue<string>().ShouldBe(turn.ConversationId);
        body.AsObject().ContainsKey("value").ShouldBeFalse();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("default-src 'none'");
    }

    // The published origin asks with the served commit's instructions, so an edit in the tree changes
    // nothing a viewer sees until published; the preview, its author's, with the tree's.
    [Fact]
    public async Task ThePreview_AsksWithTheWorkingTreesInstructions()
    {
        var preview = _apps.Preview("glucose", _id);

        var answer = _apps.AskAsync(preview, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, preview));
        var turn = await NextTurnAsync(_agent);
        await ReplyAsync(turn, "Hello there.");

        turn.AppTurn!.Instructions.ShouldBe(Tree);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // An answer names its conversation, and an ask naming it is a turn on that conversation, on the agent
    // that holds its history, answered in the same one. Each ask carries its own schema: a follow-up sending
    // none is answered in text, whatever the first asked for.
    [Fact]
    public async Task AFollowUp_IsATurnOnTheSameConversation_AnsweredInIt_ItsOwnSchemaOrNone()
    {
        var host = _apps.Published("glucose", _id);
        var session = _apps.SessionFor(AppsServer.Her, host);
        var first = _apps.AskAsync(host, AskForCarbs, session);
        var opened = await NextTurnAsync(_agent);
        await ReplyAsync(opened, """{"grams":12}""");
        var conversation = await ConversationOfAsync(await first);

        var answer = _apps.AskAsync(host, FollowUp(conversation, "And with butter?"), session);
        var followUp = await NextTurnAsync(_agent);
        await ReplyAsync(followUp, "Still twelve.");

        conversation.ShouldBe(opened.ConversationId);
        followUp.ConversationId.ShouldBe(conversation);
        followUp.AgentId.ShouldBe("jonas");
        followUp.Content.ShouldBe("And with butter?");
        followUp.ResponseFormat.ShouldBeNull();
        followUp.TurnKey.ShouldNotBe(opened.TurnKey);
        using var response = await answer;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body["conversation"]!.GetValue<string>().ShouldBe(conversation);
        body["text"]!.GetValue<string>().ShouldBe("Still twelve.");
        body.ContainsKey("value").ShouldBeFalse();
    }

    // Only the app and the viewer that opened a conversation continue it. Every other is refused alike before
    // any turn, so the answer says nothing of whose a conversation is or whether it was ever opened.
    [Theory]
    [InlineData("never opened")]
    [InlineData("another app's")]
    [InlineData("another viewer's")]
    public async Task AFollowUpOnAConversationNotTheAppsAndViewers_IsConversationExpired_BeforeAnyTurn(string whose)
    {
        var host = _apps.Published("glucose", _id);
        var conversation = await OpenedAsync(_apps, _agent, host, _apps.SessionFor(AppsServer.Her, host));
        var (asking, viewer) = whose switch
        {
            "another app's" => (_apps.Published("carbs", await InstructedAppAsync(_apps, "carbs")), AppsServer.Her),
            "another viewer's" => (host, AppsServer.Him),
            _ => (host, AppsServer.Her)
        };
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id, approved: new AppReach());

        using var response = await _apps.AskAsync(asking,
            FollowUp(whose == "never opened" ? $"{conversation}x" : conversation, "And now?"), _apps.SessionFor(viewer, asking));

        await ShouldBeExpiredAsync(response);
        await NothingAskedAsync();
    }

    // The binding lasts the server's follow-up window after the conversation's last turn, each follow-up
    // starting it again.
    [Fact]
    public async Task AFollowUpPastTheWindowAfterTheLastTurn_IsConversationExpired_BeforeAnyTurn()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var apps = await AppsServer.StartAsync(services: s =>
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AskConversations>(sp, (TimeProvider)clock)));
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);
        var conversation = await OpenedAsync(apps, agent, host, session);
        var window = apps.Settings.Asks.FollowUpWindow;

        clock.Advance(window - TimeSpan.FromMinutes(1));
        var kept = apps.AskAsync(host, FollowUp(conversation, "Still there?"), session);
        await ReplyAsync(agent, await NextTurnAsync(agent), "Yes.");
        (await kept).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(window);

        using var response = await apps.AskAsync(host, FollowUp(conversation, "And now?"), session);

        await ShouldBeExpiredAsync(response);
        await NothingAskedAsync(agent);
    }

    // The binding is kept in memory alone: a restarted server, over the same apps, continues nothing.
    [Fact]
    public async Task AFollowUpAfterTheServerRestarted_IsConversationExpired_BeforeAnyTurn()
    {
        var host = _apps.Published("glucose", _id);
        var conversation = await OpenedAsync(_apps, _agent, host, _apps.SessionFor(AppsServer.Her, host));
        await using var restarted = await AppsServer.StartAsync(configure: settings =>
        {
            CopyDirectory(_apps.Settings.StoragePath, settings.StoragePath);
            return settings;
        });
        var agent = await restarted.AgentAsync();

        using var response = await restarted.AskAsync(host, FollowUp(conversation, "And now?"),
            restarted.SessionFor(AppsServer.Her, host));

        await ShouldBeExpiredAsync(response);
        await NothingAskedAsync(agent);
    }

    // The servers an ask may use are its origin's own manifest's: the served commit's on the published
    // origin, so a tree edit widens nothing a viewer's asks reach until published; the tree's on the preview.
    [Fact]
    public async Task TheServersAnAskMayUse_AreItsOriginsOwnManifests()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", asks: """["vault","sugary"]"""), overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", asks: """["vault","homeassistant"]"""), overwrite: true);

        var published = await ServersAskedFromAsync(_apps.Published("glucose", _id));
        var preview = await ServersAskedFromAsync(_apps.Preview("glucose", _id));

        published.ShouldBe(["vault", "sugary"]);
        preview.ShouldBe(["vault", "homeassistant"]);
    }

    // A shared app's asks reach no more than an owner approved, for every viewer, its author too: the servers
    // its served manifest asks that the approval names, in the manifest's order. A server approved but not
    // asked is not offered, and an approval naming no servers grants none.
    [Fact]
    public async Task ASharedAppsAsks_AreGrantedOnlyTheServersAnOwnerApproved_ForEveryViewer()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", asks: """["vault","sugary"]"""), overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = _apps.Published("glucose", _id);
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id,
            approved: new AppReach { Reads = ["/sugary"], Asks = ["homeassistant", "sugary"] });

        var hers = await ServersAskedFromAsync(host);
        var his = await ServersAskedFromAsync(host, AppsServer.Him);
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id, approved: new AppReach { Reads = ["/sugary"] });
        var readsAlone = await ServersAskedFromAsync(host, AppsServer.Him);

        hers.ShouldBe(["sugary"]);
        his.ShouldBe(["sugary"]);
        readsAlone.ShouldBeEmpty();
    }

    // Nobody shares it: its author's asks may use every server its served manifest names.
    [Fact]
    public async Task AnUnsharedAppsAsks_AreGrantedTheWholeManifest()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", asks: """["vault","sugary"]"""), overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        (await ServersAskedFromAsync(_apps.Published("glucose", _id))).ShouldBe(["vault", "sugary"]);
    }

    // An ask with a schema is a turn whose answer must come in it: the schema rides the message as its
    // response format, as the page wrote it, and the reply's JSON is the answer's value, never its text.
    [Fact]
    public async Task AnAskWithASchema_SendsItAsTheResponseFormat_AndIsAnsweredWithTheReplysJson()
    {
        var host = _apps.Published("glucose", _id);

        var answer = _apps.AskAsync(host, AskForCarbs,
            _apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);

        turn.ResponseFormat.ShouldNotBeNull();
        turn.ResponseFormat.Name.ShouldBe("carbs");
        turn.ResponseFormat.Schema.GetRawText().ShouldBe(CarbsSchema);

        await SendAsync(turn, ReplyContentType.Text, "Let me look.", "resp_1");
        await SendAsync(turn, ReplyContentType.Text, "{\"grams\":", "resp_2");
        await SendAsync(turn, ReplyContentType.Text, "12}", "resp_2");
        await SendAsync(turn, ReplyContentType.StreamComplete, "", "resp_2", isComplete: true);

        using var response = await answer;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body["value"]!.ToJsonString().ShouldBe("""{"grams":12}""");
        body["conversation"]!.GetValue<string>().ShouldBe(turn.ConversationId);
        body.ContainsKey("text").ShouldBeFalse();
    }

    // A reply that is no JSON is no answer to a schema. What the model wrote instead reaches the author on
    // the preview, where nobody else asks; on the published origin it reaches no viewer, the author included.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplyToASchemaThatIsNoJson_IsAnInvalidReply_ItsWordsOnThePreviewAlone(bool preview)
    {
        var host = preview ? _apps.Preview("glucose", _id) : _apps.Published("glucose", _id);

        var answer = _apps.AskAsync(host, AskForCarbs,
            _apps.SessionFor(AppsServer.Her, host));
        await ReplyAsync(await NextTurnAsync(_agent), "About twelve grams.");

        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonNode.Parse(body)!.AsObject();
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidReply);
        envelope["retryable"]!.GetValue<bool>().ShouldBeFalse();
        if (preview)
        {
            envelope[AppAsks.ReplyField]!.GetValue<string>().ShouldBe("About twelve grams.");
        }
        else
        {
            body.ShouldNotContain("twelve");
        }
    }

    // A schema is held to its bound before anything is sent; one at the bound is sent, whatever the text
    // beside it, so the body's own bound never refuses what the two bounds allow.
    [Fact]
    public async Task ASchemaOverItsBound_IsRefusedBeforeAnyTurn_AndOneAtItIsSent()
    {
        var host = _apps.Published("glucose", _id);
        var bound = _apps.Settings.Asks.MaxSchemaBytes;
        var text = new string('\u00e9', _apps.Settings.Asks.MaxTextLength);

        using var over = await _apps.AskAsync(host, AskWithSchemaOf(text, bound + 1), _apps.SessionFor(AppsServer.Her, host));

        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = JsonNode.Parse(await over.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        envelope["message"]!.GetValue<string>().ShouldContain($"over the {bound} bytes");
        await NothingAskedAsync();

        var at = _apps.AskAsync(host, AskWithSchemaOf(text, bound), _apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);
        await ReplyAsync(turn, "{}");
        (await at).StatusCode.ShouldBe(HttpStatusCode.OK);
        System.Text.Encoding.UTF8.GetByteCount(turn.ResponseFormat!.Schema.GetRawText()).ShouldBe(bound);
    }

    // The model an ask is answered on is its origin's own manifest's, sent as the message's config patch: the
    // served commit's on the published origin, the tree's on the preview. Whether the agent may be patched to
    // it is the agent's to say.
    [Fact]
    public async Task TheModelAnAskNames_IsItsOriginsOwnManifests_SentAsTheConfigPatch()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", model: "z-ai/glm-5.3-flash"), overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", model: "openai/gpt-6-luna"), overwrite: true);

        var published = await TurnAskedFromAsync(_apps.Published("glucose", _id));
        var preview = await TurnAskedFromAsync(_apps.Preview("glucose", _id));

        published.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.3-flash" });
        preview.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "openai/gpt-6-luna" });
    }

    // An app whose manifest names no instructions cannot ask: refused before anything is sent.
    [Fact]
    public async Task AnAppWithoutInstructions_IsRefusedPermissionDenied_BeforeAnyTurn()
    {
        var id = AppsServer.IdIn(await _apps.AppAsync(AppsServer.Her, "plain"));
        await _apps.CreateAsync(AppsServer.Her, "/plain/index.html", "<h1>Plain</h1>");
        await _apps.ExecAsync(AppsServer.Her, "/plain", "./publish");
        var host = _apps.Published("plain", id);

        using var response = await _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        envelope["message"]!.GetValue<string>().ShouldContain("no instructions");
        await NothingAskedAsync();
    }

    // The preview never went through a publish, so its instructions are read as a publish would read them.
    [Theory]
    [InlineData(null, "are not a file of the app")]
    [InlineData("over", "are over the 16000 bytes")]
    public async Task ThePreviewsInstructions_ThatAreNoFileOrOverTheBound_AreRefusedNamingWhy(string? content, string words)
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json", Manifest("asks/other.md"), overwrite: true);
        if (content is not null)
        {
            await _apps.CreateAsync(AppsServer.Her, "/glucose/asks/other.md", new string('x', AppAsks.MaxInstructionsBytes + 1));
        }

        var preview = _apps.Preview("glucose", _id);

        using var response = await _apps.AskAsync(preview, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, preview));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        envelope["message"]!.GetValue<string>().ShouldContain($"the instructions asks/other.md {words}");
        await NothingAskedAsync();
    }

    // Nobody signed in, a signed-in person who may not open the app, and another origin's page are answered
    // the one 404 a page gets, whatever the ask's body: nothing is sent for them.
    [Fact]
    public async Task AnAskByAnyoneWhoMayNotOpenTheApp_IsTheOne404_WhateverItsBody()
    {
        var host = _apps.Published("glucose", _id);
        var page = await _apps.GetAsync(host, "/missing.css", _apps.SessionFor(AppsServer.Him, host));

        using var signedOut = await _apps.AskAsync(host, """{"text":"Hi"}""", session: null);
        using var stranger = await _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Him, host));
        using var strangerMalformed = await _apps.AskAsync(host, "not json", _apps.SessionFor(AppsServer.Him, host));
        using var foreign = await _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host),
            origin: $"https://other-abc123.{AppsServer.AppsDomain}");

        foreach (var answer in new[] { signedOut, stranger, strangerMalformed, foreign })
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await answer.Content.ReadAsStringAsync()).ShouldBe(await page.Content.ReadAsStringAsync());
        }

        await NothingAskedAsync();
    }

    [Theory]
    [InlineData("""{"text":""}""", "1 to 8000 characters")]
    [InlineData("""{"text":"Hi","stream":true}""", "stream")]
    [InlineData("""{"text":"Hi","conversation":5}""", "conversation")]
    [InlineData("""not json""", "JSON object")]
    [InlineData("""{"text":"Hi","schema":{"name":"carbs","schema":[]}}""", "a JSON object")]
    [InlineData("""{"text":"Hi","schema":{"name":"carbs and grams","schema":{}}}""", "letters, digits, _ and -")]
    public async Task AnAskOutsideItsShape_IsAnInvalidArgument_BeforeAnyTurn(string body, string words)
    {
        var host = _apps.Published("glucose", _id);

        using var response = await _apps.AskAsync(host, body, _apps.SessionFor(AppsServer.Her, host));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        envelope["message"]!.GetValue<string>().ShouldContain(words);
        await NothingAskedAsync();
    }

    // What the agent refused is the app's error in the tools' envelope, the refusal's sentence its message:
    // a standing refusal permission_denied, an unreadable household worth asking again. Anything else the
    // agent reported is the exception it caught, kept from the page.
    [Theory]
    [InlineData("agent-gone", ToolError.Codes.PermissionDenied, "'jonas'")]
    [InlineData("apps-off", ToolError.Codes.PermissionDenied, "Built apps are not turned on")]
    [InlineData("unchecked", ToolError.Codes.TransientDependency, "could not be checked")]
    [InlineData("provider", ToolError.Codes.TransientDependency, "could not answer this ask")]
    public async Task ATurnTheAgentRefusedOrFailed_IsTheAppsEnvelope(string refusal, string code, string words)
    {
        var host = _apps.Published("glucose", _id);
        var reason = refusal switch
        {
            "agent-gone" => RefusalAnswers.AgentGone("jonas"),
            "apps-off" => RefusalAnswers.AppsOff,
            "unchecked" => RefusalAnswers.Unchecked,
            _ => "Provider returned 402: {\"error\":\"credits\"}"
        };

        var answer = _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);
        await SendAsync(turn, ReplyContentType.Error, reason);
        await SendAsync(turn, ReplyContentType.StreamComplete, "", isComplete: true);

        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonNode.Parse(body)!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(code);
        envelope["message"]!.GetValue<string>().ShouldContain(words);
        body.ShouldNotContain("credits");
    }

    // A turn that ends with no text — an ask whose model stopped on calls nobody could approve ends so — is
    // answered as no answer, saying the likely why, so a check's report tells its author what to change.
    [Fact]
    public async Task ATurnEndingWithNoText_IsNoAnswer_NamingCallsNobodyCouldApprove()
    {
        var preview = _apps.Preview("glucose", _id);

        var answer = _apps.AskAsync(preview, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, preview));
        var turn = await NextTurnAsync(_agent);
        await SendAsync(turn, ReplyContentType.StreamComplete, "", isComplete: true);

        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.TransientDependency);
        envelope["message"]!.GetValue<string>().ShouldContain("no answer");
        envelope["hint"]!.GetValue<string>().ShouldContain("approval");
    }

    // What the agent said of a turn it failed — often the provider's own body, a strict schema it refused
    // among them — is the exception it caught, kept from a viewer; its author sees it on the preview alone, so
    // a check can say why and the schema be fixed.
    [Fact]
    public async Task AFailedTurnsWords_RideOnThePreviewAlone()
    {
        const string said = "An error occurred: Provider returned 400: {\"error\":\"invalid schema: 'minimum' is not supported\"}";

        async Task<JsonNode> FailedFromAsync(string host)
        {
            var answer = _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host));
            var turn = await NextTurnAsync(_agent);
            await SendAsync(turn, ReplyContentType.Error, said);
            await SendAsync(turn, ReplyContentType.StreamComplete, "", isComplete: true);
            using var response = await answer;
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        var previewed = await FailedFromAsync(_apps.Preview("glucose", _id));
        var published = await FailedFromAsync(_apps.Published("glucose", _id));

        previewed["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.TransientDependency);
        previewed[AppAsks.FailureField]!.GetValue<string>().ShouldBe(said);
        published.AsObject().ContainsKey(AppAsks.FailureField).ShouldBeFalse();
        published.ToJsonString().ShouldNotContain("invalid schema");
    }

    // The page never waits on a turn without end: past the server's wait it is told so, and the turn is
    // cancelled, naming the agent, since the agent groups an app's messages under it.
    [Fact]
    public async Task ATurnThatOutlivesTheWait_IsCancelledNamingTheAgent_AndThePageToldItTimedOut()
    {
        var clock = new ArmedClock();
        await using var apps = await AppsServer.StartAsync(services: s =>
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AgentAsks>(sp, (TimeProvider)clock)));
        var agent = await apps.AgentAsync();
        var id = await InstructedAppAsync(apps);
        var host = apps.Published("glucose", id);

        var answer = apps.AskAsync(host, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(agent);
        await clock.AdvancePastAsync(apps.Settings.Asks.Timeout);

        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errorCode"]!.GetValue<string>()
            .ShouldBe(ToolError.Codes.Timeout);
        var cancel = await NextTurnAsync(agent);
        cancel.Sender.ShouldBe(ChannelProtocol.SystemSender);
        cancel.ConversationId.ShouldBe(turn.ConversationId);
        cancel.AgentId.ShouldBe("jonas");
    }

    // An answer nobody will read is not worth the rest of the turn: the page giving up cancels it.
    [Fact]
    public async Task APageThatGivesUp_CancelsTheTurn_NamingTheAgent()
    {
        var host = _apps.Published("glucose", _id);
        using var giveUp = new CancellationTokenSource();

        var answer = _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host), ct: giveUp.Token);
        var turn = await NextTurnAsync(_agent);
        await giveUp.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(answer);
        var cancel = await NextTurnAsync(_agent);
        cancel.Sender.ShouldBe(ChannelProtocol.SystemSender);
        cancel.ConversationId.ShouldBe(turn.ConversationId);
        cancel.AgentId.ShouldBe("jonas");
    }

    // Nobody polling the channel is answered at once, never buffered for an agent that may never come.
    [Fact]
    public async Task AnAskNobodyIsListeningFor_IsUnavailable()
    {
        await using var apps = await AppsServer.StartAsync();
        var id = await InstructedAppAsync(apps);
        var host = apps.Published("glucose", id);

        using var response = await apps.AskAsync(host, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, host));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.TransientDependency);
        envelope["retryable"]!.GetValue<bool>().ShouldBeTrue();
    }

    // A check's page asks as the person whose turn ran `./check`, on the preview: its session holds no `apps`
    // assertion, only that call's own token, which goes in the assertion's place for the agent to believe, and
    // never beside one.
    [Fact]
    public async Task AnAskFromACheck_IsSentOnItsCallToken_InPlaceOfAnAssertion_WithThePreviewsInstructions()
    {
        var preview = _apps.Preview("glucose", _id);
        var session = _apps.CheckSessionFor(AppsServer.Her, preview, "a-call-token", _apps.Clock.GetUtcNow().AddMinutes(1));

        var answer = _apps.AskAsync(preview, """{"text":"Hi"}""", session);
        var turn = await NextTurnAsync(_agent);
        await ReplyAsync(turn, "Hello there.");

        turn.CheckCallToken.ShouldBe("a-call-token");
        turn.Assertion.ShouldBeNull();
        turn.AgentId.ShouldBe("jonas");
        turn.Principal.ShouldBe(new Principal(AppChannels.Apps, AppsServer.Her.UserId, AppsServer.Her));
        turn.AppTurn!.Instructions.ShouldBe(Tree);
        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // A check's asks are real turns, so they count toward the app's cap as anyone's do, read on the check's own
    // proof: past it, the check's page is refused as a viewer's would be, and its author viewing the published app too.
    [Fact]
    public async Task AChecksAsks_CountTowardTheAppsCap()
    {
        await using var apps = await CappedAsync(1, new FakeTimeProvider(_capStart));
        var agent = await apps.AgentAsync();
        var id = await InstructedAppAsync(apps);
        var preview = apps.Preview("glucose", id);
        var host = apps.Published("glucose", id);
        var check = apps.CheckSessionFor(AppsServer.Her, preview, "a-call-token", apps.Clock.GetUtcNow().AddMinutes(1));

        await AnsweredAsync(apps, agent, preview, check);
        using var checkedAgain = await apps.AskAsync(preview, """{"text":"Hi"}""", check);
        using var viewed = await apps.AskAsync(host, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, host));

        await ShouldBeCappedAsync(checkedAgain, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        await ShouldBeCappedAsync(viewed, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        await NothingAskedAsync(agent);
    }

    // A check is stopped at its deadline and its call token is good no longer, so its ask waits no longer than
    // the check has left, however long an ask may otherwise wait.
    [Fact]
    public async Task AnAskFromACheck_WaitsNoLongerThanTheCheckHasLeft()
    {
        var clock = new ArmedClock();
        await using var apps = await AppsServer.StartAsync(services: s =>
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AgentAsks>(sp, (TimeProvider)clock)));
        var agent = await apps.AgentAsync();
        var id = await InstructedAppAsync(apps);
        var preview = apps.Preview("glucose", id);
        var left = TimeSpan.FromSeconds(30);
        var check = apps.CheckSessionFor(AppsServer.Her, preview, "a-call-token", clock.GetUtcNow() + left);

        var answer = apps.AskAsync(preview, """{"text":"Hi"}""", check);
        var turn = await NextTurnAsync(agent);
        await clock.AdvancePastAsync(left);

        using var response = await answer;
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout, body);
        JsonNode.Parse(body)!["message"]!.GetValue<string>().ShouldContain("within 30 seconds");
        (await NextTurnAsync(agent)).ConversationId.ShouldBe(turn.ConversationId);
    }

    // The cap is the household's on one app: every ask sent to the agent counts, and past the limit an ask is
    // refused before anything is sent, naming the limit and when an ask is counted again. Asking again sooner
    // changes nothing, so it is no retry.
    [Fact]
    public async Task AnAskPastTheDailyCap_IsRefusedNamingTheLimit_BeforeAnyTurn()
    {
        var clock = new FakeTimeProvider(_capStart);
        await using var apps = await CappedAsync(2, clock);
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);
        await AnsweredAsync(apps, agent, host, session);
        clock.Advance(TimeSpan.FromHours(1));
        await AnsweredAsync(apps, agent, host, session);

        using var response = await apps.AskAsync(host, """{"text":"Hi"}""", session);

        await ShouldBeCappedAsync(response, 2, freesAt: _capStart + TimeSpan.FromDays(1));
        await NothingAskedAsync(agent);
    }

    // An ask is counted when it is sent: one refused for its body, for its conversation, or because nobody was
    // listening reached no model, and the app keeps its whole cap.
    [Fact]
    public async Task AnAskRefusedBeforeItWasSent_CountsNothing()
    {
        await using var apps = await CappedAsync(1, new FakeTimeProvider(_capStart));
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);

        using var unheard = await apps.AskAsync(host, """{"text":"Hi"}""", session);
        var agent = await apps.AgentAsync();
        using var malformed = await apps.AskAsync(host, """{"text":""}""", session);
        using var expired = await apps.AskAsync(host, FollowUp("never-opened", "And now?"), session);
        await AnsweredAsync(apps, agent, host, session);
        using var capped = await apps.AskAsync(host, """{"text":"Hi"}""", session);

        unheard.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        await ShouldBeCappedAsync(capped, 1, freesAt: _capStart + TimeSpan.FromDays(1));
    }

    // One count per app of a household: whoever views it, on the published origin or the preview. Another app
    // of the household, and another household's app, count their own.
    [Fact]
    public async Task EveryViewerOfAnApp_SharesOneCount_WhileAnotherAppOrHouseholdHasItsOwn()
    {
        await using var apps = await CappedAsync(1, new FakeTimeProvider(_capStart));
        var agent = await apps.AgentAsync();
        var id = await InstructedAppAsync(apps);
        var host = apps.Published("glucose", id);
        var preview = apps.Preview("glucose", id);
        apps.Spaces.Share(AppsServer.Her.TenantId, id, approved: new AppReach());
        var other = apps.Published("carbs", await InstructedAppAsync(apps, "carbs"));
        var neighbours = apps.Published("glucose", await InstructedAppAsync(apps, author: AppsServer.Neighbour));
        await AnsweredAsync(apps, agent, host, apps.SessionFor(AppsServer.Her, host));

        using var his = await apps.AskAsync(host, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Him, host));
        using var previewed = await apps.AskAsync(preview, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, preview));
        await ShouldBeCappedAsync(his, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        await ShouldBeCappedAsync(previewed, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        await AnsweredAsync(apps, agent, other, apps.SessionFor(AppsServer.Her, other));
        await AnsweredAsync(apps, agent, neighbours, apps.SessionFor(AppsServer.Neighbour, neighbours));
    }

    // The window rolls: an ask stops counting a day after the minute it was sent in.
    [Fact]
    public async Task AnAskADayOld_StopsCounting()
    {
        var clock = new FakeTimeProvider(_capStart);
        await using var apps = await CappedAsync(1, clock);
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);
        clock.Advance(TimeSpan.FromSeconds(30));
        await AnsweredAsync(apps, agent, host, session);

        clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        using var capped = await apps.AskAsync(host, """{"text":"Hi"}""", session);
        await ShouldBeCappedAsync(capped, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        clock.Advance(TimeSpan.FromSeconds(30));

        await AnsweredAsync(apps, agent, host, session);
    }

    // The counts are kept on the server's volume, so a restart lifts no cap.
    [Fact]
    public async Task TheCounts_OutliveARestart()
    {
        var clock = new FakeTimeProvider(_capStart);
        await using var apps = await CappedAsync(1, clock);
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        await AnsweredAsync(apps, agent, host, apps.SessionFor(AppsServer.Her, host));

        await using var restarted = await CappedAsync(1, clock, from: apps.Settings.StoragePath);
        using var response = await restarted.AskAsync(host, """{"text":"Hi"}""", restarted.SessionFor(AppsServer.Her, host));

        await ShouldBeCappedAsync(response, 1, freesAt: _capStart + TimeSpan.FromDays(1));
    }

    private static readonly DateTimeOffset _capStart = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    // An owner's caps stand before the deployment's: an app's own cap first, else its household's default,
    // else Asks:DailyCap — here 0 for one app, 1 for the household's other app, the deployment's 2 for the
    // neighbour's app, whose household set none.
    [Fact]
    public async Task TheLimit_IsTheAppsOwnCap_ElseItsHouseholds_ElseTheDeployments()
    {
        await using var apps = await CappedAsync(2, new FakeTimeProvider(_capStart));
        var agent = await apps.AgentAsync();
        var ownId = await InstructedAppAsync(apps);
        var own = apps.Published("glucose", ownId);
        var households = apps.Published("carbs", await InstructedAppAsync(apps, "carbs"));
        var neighbours = apps.Published("glucose", await InstructedAppAsync(apps, author: AppsServer.Neighbour));
        apps.AskCaps.Set(AppsServer.Her.TenantId, new AppAsksSection { DailyCap = 1, Apps = new Dictionary<string, int> { [ownId] = 0 } });

        using var none = await apps.AskAsync(own, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, own));
        await AnsweredAsync(apps, agent, households, apps.SessionFor(AppsServer.Her, households));
        using var one = await apps.AskAsync(households, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Her, households));
        await AnsweredAsync(apps, agent, neighbours, apps.SessionFor(AppsServer.Neighbour, neighbours));
        await AnsweredAsync(apps, agent, neighbours, apps.SessionFor(AppsServer.Neighbour, neighbours));
        using var two = await apps.AskAsync(neighbours, """{"text":"Hi"}""", apps.SessionFor(AppsServer.Neighbour, neighbours));

        await ShouldBeCappedAsync(none, 0, freesAt: null);
        await ShouldBeCappedAsync(one, 1, freesAt: _capStart + TimeSpan.FromDays(1));
        await ShouldBeCappedAsync(two, 2, freesAt: _capStart + TimeSpan.FromDays(1));
        await NothingAskedAsync(agent);
    }

    // A household's caps that cannot be read refuse the ask, never fall back to the deployment's, which
    // could lift a cap an owner set lower: an outage is a transient dependency, a refusal of the viewer's
    // proof a denial. Neither sends nor counts anything.
    [Theory]
    [InlineData("down", HttpStatusCode.ServiceUnavailable, ToolError.Codes.TransientDependency)]
    [InlineData("slow", HttpStatusCode.ServiceUnavailable, ToolError.Codes.TransientDependency)]
    [InlineData("refused", HttpStatusCode.UnprocessableEntity, ToolError.Codes.PermissionDenied)]
    public async Task CapsThatCannotBeRead_RefuseTheAsk_SendingAndCountingNothing(string how, HttpStatusCode status, string code)
    {
        await using var apps = await CappedAsync(1, new FakeTimeProvider(_capStart));
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);
        apps.AskCaps.Down = how == "down";
        apps.AskCaps.Refuses = how == "refused";
        apps.AskCaps.TimesOut = how == "slow";

        using var response = await apps.AskAsync(host, """{"text":"Hi"}""", session);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        JsonNode.Parse(body)!["errorCode"]!.GetValue<string>().ShouldBe(code);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        await NothingAskedAsync(agent);
        apps.AskCaps.Down = apps.AskCaps.Refuses = apps.AskCaps.TimesOut = false;
        await AnsweredAsync(apps, agent, host, session);
    }

    // An ask's images are kept on the apps server for the app's household and named on the message by
    // reference, each the type its bytes are, whatever the page called it. The agent fetches them on the apps
    // channel, signed as it signs the turn's calls: anyone of the household is handed them, another
    // household's turn or a call for nobody nothing.
    [Fact]
    public async Task AnAsksImages_AreNamedOnTheMessage_AsTheirBytesAre_AndFetchedForTheAppsHouseholdAlone()
    {
        var host = _apps.Published("glucose", _id);
        byte[] plate = Png(1), side = Jpeg(2);

        var answer = _apps.AskWithImagesAsync(host, """{"text":"What is on the plate?"}""",
            [("plate.png", "image/png", plate), ("side.jpg", "application/octet-stream", side)],
            _apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);

        turn.Content.ShouldBe("What is on the plate?");
        var images = turn.Attachments.ShouldNotBeNull();
        images.Select(a => (a.FileName, a.MediaType, a.SizeBytes))
            .ShouldBe([("plate.png", "image/png", plate.Length), ("side.jpg", "image/jpeg", side.Length)]);
        (await FetchAsync(images[0], turn, AppsServer.Her)).ShouldBe(plate);
        (await FetchAsync(images[1], turn, AppsServer.Him)).ShouldBe(side);
        (await FetchAsync(images[0], turn, AppsServer.Neighbour)).ShouldBeNull();
        (await _agent.FetchAttachmentAsync(images[0].Id, null, CancellationToken.None)).ShouldBeNull();
        await ReplyAsync(turn, "Rice and an egg.");
        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // Past a limit, or no image the deployment takes, an ask is refused whole before any turn: nothing is
    // kept, nothing sent, and nothing counted, so the app's whole cap is still there for the ask it sends next.
    [Theory]
    [InlineData("too many", "At most 4 images")]
    [InlineData("too large", "each image is at most 5242880 bytes")]
    [InlineData("no image", "is not a JPEG, PNG, WebP or GIF image")]
    [InlineData("a stray field", "one ask field")]
    [InlineData("no ask", "one ask field")]
    public async Task AnAsksImagesPastTheLimits_AreRefusedBeforeAnyTurn_KeepingAndCountingNothing(string what, string words)
    {
        await using var apps = await CappedAsync(1, new FakeTimeProvider(_capStart));
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var session = apps.SessionFor(AppsServer.Her, host);
        var form = new MultipartFormDataContent();
        if (what != "no ask")
        {
            form.Add(new StringContent("""{"text":"What is this?"}"""), AppAsks.AskField);
        }

        if (what == "a stray field")
        {
            form.Add(new StringContent("Hi"), "text");
        }

        IEnumerable<byte[]> images = what switch
        {
            "too many" => [.. Enumerable.Range(0, 5).Select(i => Png((byte)i))],
            "too large" => [[.. Png(1), .. new byte[apps.Settings.Asks.MaxImageBytes]]],
            "no image" => [[.. "%PDF-1.7"u8]],
            _ => [Png(1)]
        };
        foreach (var image in images)
        {
            form.Add(new ByteArrayContent(image), AppAsks.FilesField, "photo.png");
        }

        using var response = await apps.AskAsync(host, form, session);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        var envelope = JsonNode.Parse(body)!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        envelope["message"]!.GetValue<string>().ShouldContain(words);
        await NothingAskedAsync(agent);
        StoredUploads(apps).ShouldBeEmpty();
        await AnsweredAsync(apps, agent, host, session);
    }

    // An ask nobody was listening for reached no model, so the images it brought are not kept for a
    // retention nothing will ever hydrate within: a page retrying through an outage fills no volume.
    [Fact]
    public async Task AnAskNobodyIsListeningFor_KeepsNoneOfItsImages()
    {
        await using var apps = await AppsServer.StartAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));

        using var response = await apps.AskWithImagesAsync(host, """{"text":"What is this?"}""",
            [("plate.png", "image/png", Png(1))], apps.SessionFor(AppsServer.Her, host));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        StoredUploads(apps).ShouldBeEmpty();
    }

    // The images wait for the agent as long as the deployment keeps an attachment anywhere, then the store's
    // sweep takes them, and a history still naming one hydrates a placeholder.
    [Fact]
    public async Task AnAsksImages_AreSweptAfterTheRetention()
    {
        var clock = new FakeTimeProvider(_capStart);
        await using var apps = await AppsServer.StartAsync(services: s => s.AddSingleton(sp => new UploadStore(
            sp.GetRequiredService<IUploadStoreSettings>(), sp.GetRequiredService<RetentionSettings>(), clock,
            NullLogger<UploadStore>.Instance)));
        var agent = await apps.AgentAsync();
        var host = apps.Published("glucose", await InstructedAppAsync(apps));
        var answer = apps.AskWithImagesAsync(host, """{"text":"What is this?"}""",
            [("plate.png", "image/png", Png(1))], apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(agent);
        await ReplyAsync(agent, turn, "Rice.");
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
        var store = apps.Services.GetRequiredService<UploadStore>();
        var image = turn.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();

        store.Sweep().ShouldBe(0);
        clock.Advance(apps.Settings.Retention.AttachmentRetention + TimeSpan.FromMinutes(1));

        store.Sweep().ShouldBe(1);
        (await FetchAsync(agent, image, turn, AppsServer.Her)).ShouldBeNull();
        StoredUploads(apps).ShouldBeEmpty();
    }

    // Where the upload store keeps an ask's images: on the apps volume, beside the households.
    private static IReadOnlyList<string> StoredUploads(AppsServer apps)
    {
        var uploads = Path.Combine(apps.Settings.StoragePath, ".uploads");
        return Directory.Exists(uploads) ? Directory.GetFiles(uploads, "*", SearchOption.AllDirectories) : [];
    }

    private static byte[] Png(byte seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, seed, seed];

    private static byte[] Jpeg(byte seed) => [0xFF, 0xD8, 0xFF, 0xE0, seed, seed];

    private Task<byte[]?> FetchAsync(AttachmentReference image, ChannelMessage turn, Account person) =>
        FetchAsync(_agent, image, turn, person);

    // An image fetched as the agent fetches it for a turn of this person's: its context in the call's _meta,
    // signed by the agent's call tokens for the identity the turn was admitted as.
    private static Task<byte[]?> FetchAsync(McpChannelConnection agent, AttachmentReference image, ChannelMessage turn, Account person) =>
        agent.FetchAttachmentAsync(image.Id, new ConversationContext(
            turn.AgentId!, turn.ConversationId, person.UserId, new ReplyTarget(AppChannels.Apps, turn.ConversationId))
        {
            Identity = IdentityClaims.Person(person.UserId, person.TenantId, AppChannels.Apps, [])
        }, CancellationToken.None);

    // A server whose asks are capped at so many a day, counted on the clock given (the counts' alone: the
    // server's own stays real, so an agent polling in real time never looks gone), over a copy of a volume.
    private static Task<AppsServer> CappedAsync(int cap, TimeProvider clock, string? from = null) =>
        AppsServer.StartAsync(
            configure: settings =>
            {
                if (from is not null)
                {
                    CopyDirectory(from, settings.StoragePath);
                }

                return settings with { Asks = settings.Asks with { DailyCap = cap } };
            },
            services: s => s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AskCounts>(sp, clock)));

    private static async Task AnsweredAsync(AppsServer apps, McpChannelConnection agent, string host, string session)
    {
        var answer = apps.AskAsync(host, """{"text":"How was my morning?"}""", session);
        await ReplyAsync(agent, await NextTurnAsync(agent), "Quietly.");
        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task ShouldBeCappedAsync(HttpResponseMessage response, int limit, DateTimeOffset? freesAt)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body);
        var envelope = JsonNode.Parse(body)!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.QuotaExceeded);
        envelope["retryable"]!.GetValue<bool>().ShouldBeFalse();
        envelope["message"]!.GetValue<string>().ShouldContain($"daily limit of {limit} asks");
        if (freesAt is { } at)
        {
            envelope[AppAsks.FreesAtField]!.GetValue<DateTimeOffset>().ShouldBe(at);
        }
        else
        {
            envelope.AsObject().ContainsKey(AppAsks.FreesAtField).ShouldBeFalse();
        }
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private const string CarbsSchema =
        """{"type":"object","properties":{"grams":{"type":"number","minimum":0}},"required":["grams"],"additionalProperties":false}""";

    private const string AskForCarbs = """{"text":"Bread","schema":{"name":"carbs","schema":""" + CarbsSchema + "}}";

    private static string Manifest(string instructions, string asks = "[]", string? model = null) =>
        $$"""{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","instructions":"{{instructions}}","asks":{{asks}}{{(model is null ? "" : $",\"model\":\"{model}\"")}}}""";

    // An ask of the text with a schema of exactly so many bytes, padded in its description.
    private static string AskWithSchemaOf(string text, int bytes)
    {
        const string around = """{"type":"object","description":""}""";
        var schema = around.Insert(around.Length - 2, new string('x', bytes - around.Length));
        return new JsonObject
        {
            ["text"] = text,
            ["schema"] = new JsonObject { ["name"] = "padded", ["schema"] = JsonNode.Parse(schema) }
        }.ToJsonString();
    }

    private async Task<IReadOnlyList<string>> ServersAskedFromAsync(string host, Account? viewer = null) =>
        (await TurnAskedFromAsync(host, viewer)).AppTurn!.Servers;

    private async Task<ChannelMessage> TurnAskedFromAsync(string host, Account? viewer = null)
    {
        var answer = _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(viewer ?? AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);
        await ReplyAsync(turn, "Hello there.");
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
        return turn;
    }

    // An app whose published instructions are Published, and its tree's since edited to Tree.
    private static async Task<string> InstructedAppAsync(AppsServer apps, string slug = "glucose", Account? author = null)
    {
        var person = author ?? AppsServer.Her;
        var id = AppsServer.IdIn(await apps.AppAsync(person, slug));
        await apps.CreateAsync(person, $"/{slug}/app.json", Manifest("asks/glucose.md"), overwrite: true);
        await apps.CreateAsync(person, $"/{slug}/index.html", "<h1>Glucose</h1>");
        await apps.CreateAsync(person, $"/{slug}/asks/glucose.md", Published);
        await apps.ExecAsync(person, $"/{slug}", "./publish");
        await apps.CreateAsync(person, $"/{slug}/asks/glucose.md", Tree, overwrite: true);
        return id;
    }

    private static string FollowUp(string conversation, string text) =>
        new JsonObject { ["text"] = text, ["conversation"] = conversation }.ToJsonString();

    // The conversation a first ask from this session opened, its turn answered.
    private static async Task<string> OpenedAsync(AppsServer apps, McpChannelConnection agent, string host, string session)
    {
        var answer = apps.AskAsync(host, """{"text":"How was my morning?"}""", session);
        await ReplyAsync(agent, await NextTurnAsync(agent), "Quietly.");
        return await ConversationOfAsync(await answer);
    }

    private static async Task<string> ConversationOfAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            return JsonNode.Parse(body)!["conversation"]!.GetValue<string>();
        }
    }

    private static async Task ShouldBeExpiredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Gone, body);
        var envelope = JsonNode.Parse(body)!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.ConversationExpired);
        envelope["retryable"]!.GetValue<bool>().ShouldBeFalse();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private static void CopyDirectory(string from, string to) =>
        Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList().ForEach(file =>
        {
            var copy = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        });

    private string AssertionOf(string session, string host) =>
        _apps.Sessions.Find(session, AppOrigin.Parse(host, AppsServer.AppsDomain)!)!.Proof
            .ShouldBeOfType<global::McpServerApps.Serving.ViewerProof.Assertion>().Token;

    private Task NothingAskedAsync() => NothingAskedAsync(_agent);

    private static async Task NothingAskedAsync(McpChannelConnection agent)
    {
        using var settled = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(async () => await agent.Messages.FirstAsync(settled.Token));
    }

    private static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(_wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

    private Task ReplyAsync(ChannelMessage turn, string text) => ReplyAsync(_agent, turn, text);

    private static async Task ReplyAsync(McpChannelConnection agent, ChannelMessage turn, string text)
    {
        await SendAsync(agent, turn, ReplyContentType.Text, text);
        await SendAsync(agent, turn, ReplyContentType.StreamComplete, "", isComplete: true);
    }

    private Task SendAsync(
        ChannelMessage turn, ReplyContentType type, string content, string messageId = "resp_2", bool isComplete = false) =>
        SendAsync(_agent, turn, type, content, messageId, isComplete);

    private static Task SendAsync(
        McpChannelConnection agent, ChannelMessage turn, ReplyContentType type, string content, string messageId = "resp_2",
        bool isComplete = false) =>
        agent.SendReplyAsync(new SendReplyParams
        {
            ConversationId = turn.ConversationId,
            Content = content,
            ContentType = type,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turn.TurnKey,
            AgentInitiated = false
        }, CancellationToken.None);
}