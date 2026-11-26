using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Domain.Security;
using Domain.Tools;
using Infrastructure.Clients.Channels;
using McpServerApps.Asks;
using Microsoft.Extensions.DependencyInjection;
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

    // A shared app's asks reach no more than an owner approved, for every viewer, its author too; an approval
    // says nothing of asks yet, so they are granted no server.
    [Fact]
    public async Task ASharedAppsAsks_AreGrantedNoServerAnOwnerDidNotApprove()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            Manifest("asks/glucose.md", asks: """["vault"]"""), overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id, approved: new AppReach { Reads = ["/sugary"] });

        (await ServersAskedFromAsync(_apps.Published("glucose", _id))).ShouldBeEmpty();
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
    [InlineData("""{"text":"Hi","conversation":"c"}""", "conversation")]
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

    // A check's session holds no `apps` assertion, only the call token that ran it, and an ask is never
    // sent unsigned.
    [Fact]
    public async Task AnAskFromACheck_IsRefused_BeforeAnyTurn()
    {
        var preview = _apps.Preview("glucose", _id);
        var session = _apps.Sessions.OpenCheck(AppsServer.Her, AppOrigin.Parse(preview, AppsServer.AppsDomain)!, "a-call-token",
            _apps.Clock.GetUtcNow().AddMinutes(1));

        using var response = await _apps.AskAsync(preview, """{"text":"Hi"}""", session);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errorCode"]!.GetValue<string>()
            .ShouldBe(ToolError.Codes.PermissionDenied);
        await NothingAskedAsync();
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

    private async Task<IReadOnlyList<string>> ServersAskedFromAsync(string host) =>
        (await TurnAskedFromAsync(host)).AppTurn!.Servers;

    private async Task<ChannelMessage> TurnAskedFromAsync(string host)
    {
        var answer = _apps.AskAsync(host, """{"text":"Hi"}""", _apps.SessionFor(AppsServer.Her, host));
        var turn = await NextTurnAsync(_agent);
        await ReplyAsync(turn, "Hello there.");
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
        return turn;
    }

    // An app whose published instructions are Published, and its tree's since edited to Tree.
    private static async Task<string> InstructedAppAsync(AppsServer apps)
    {
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        await apps.CreateAsync(AppsServer.Her, "/glucose/app.json", Manifest("asks/glucose.md"), overwrite: true);
        await apps.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Glucose</h1>");
        await apps.CreateAsync(AppsServer.Her, "/glucose/asks/glucose.md", Published);
        await apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await apps.CreateAsync(AppsServer.Her, "/glucose/asks/glucose.md", Tree, overwrite: true);
        return id;
    }

    private string AssertionOf(string session, string host) =>
        _apps.Sessions.Find(session, AppOrigin.Parse(host, AppsServer.AppsDomain)!)!.Proof
            .ShouldBeOfType<global::McpServerApps.Serving.ViewerProof.Assertion>().Token;

    private async Task NothingAskedAsync()
    {
        using var settled = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(async () => await _agent.Messages.FirstAsync(settled.Token));
    }

    private static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(_wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

    private async Task ReplyAsync(ChannelMessage turn, string text)
    {
        await SendAsync(turn, ReplyContentType.Text, text);
        await SendAsync(turn, ReplyContentType.StreamComplete, "", isComplete: true);
    }

    private Task SendAsync(
        ChannelMessage turn, ReplyContentType type, string content, string messageId = "resp_2", bool isComplete = false) =>
        _agent.SendReplyAsync(new SendReplyParams
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