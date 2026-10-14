using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Estimation;
using Infrastructure.Clients.Channels;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A described meal, estimated by the sugary agent: the app posts it as it always did, the agent
// receives it as a channel message, answers in chunks, and the app gets the estimate back. The agent
// is played over MCP by its own channel connection, so everything asserted here crossed the wire
// both ways — the format on the message, the reply as chunks, the HTTP answer the app reads.
public class SugaryEstimateApiTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    // Toast as she stated it (high confidence, kept as given) and a juice judged by eye (medium,
    // widened to 0.6–1.6 of the best weight). The model gives no totals: the server sums them.
    private const string Estimate =
        """
        {"items":[
          {"name":"white toast","portion":"2 slices","gramsLow":60,"grams":60,"gramsHigh":60,"carbsPer100g":45,"confidence":"high","note":null},
          {"name":"orange juice","portion":"a glass","gramsLow":190,"grams":200,"gramsHigh":210,"carbsPer100g":10,"confidence":"medium","note":"could be sweetened"}
        ],
        "questions":[{"text":"How big was the glass?","options":["small (150 ml)","large (300 ml)"]}],
        "notes":["Mostly fast sugars."]}
        """;

    [Fact]
    public async Task ADescribedMeal_IsEstimatedByTheAgent_WithTheTotalsAndRangesComputedByTheServer()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("two slices of toast and a glass of orange juice"));
        var turn = await NextTurnAsync(agent);

        turn.ResponseFormat.ShouldNotBeNull();
        turn.ResponseFormat.Name.ShouldBe(MealEstimateSchema.Name);
        JsonElement.DeepEquals(turn.ResponseFormat.Schema, MealEstimateSchema.Format.Schema).ShouldBeTrue();
        turn.Content.ShouldContain("There is no photo of this meal, only the description below.");
        turn.Content.ShouldContain("The person's description:\ntwo slices of toast and a glass of orange juice");
        turn.Attachments.ShouldBeNull();
        // Which agent answers is the deployment's routing (agentDefaults), not the channel's to say.
        turn.AgentId.ShouldBeNull();

        await ReplyAsync(agent, turn, Estimate[..40], Estimate[40..200], Estimate[200..]);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var estimate = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        var items = estimate["items"]!.AsArray();
        items.Count.ShouldBe(2);

        ShouldBeInterval(items[0]!["grams"], 60, 60, 60);
        ShouldBeInterval(items[0]!["carbs"], 27, 27, 27);
        items[0]!["confidence"]!.GetValue<string>().ShouldBe("high");

        ShouldBeInterval(items[1]!["grams"], 120, 200, 320);
        ShouldBeInterval(items[1]!["carbs"], 12, 20, 32);
        items[1]!["confidence"]!.GetValue<string>().ShouldBe("medium");
        items[1]!["note"]!.GetValue<string>().ShouldBe("could be sweetened");

        ShouldBeInterval(estimate["total"], 39, 47, 59);
        foreach (var bound in (string[])["low", "best", "high"])
        {
            estimate["total"]![bound]!.GetValue<double>()
                .ShouldBe(items.Sum(item => item!["carbs"]![bound]!.GetValue<double>()), bound);
        }

        estimate["questions"]![0]!["options"]!.AsArray().Count.ShouldBe(2);
        estimate["notes"]![0]!.GetValue<string>().ShouldBe("Mostly fast sugars.");
    }

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData("en", "English")]
    public async Task TheAppsLanguage_ReachesTheTurn(string language, string named)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella", language));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldEndWith($"Write every name, portion, note and question in {named}.");
        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The format applies to the final answer. A turn that says something and calls a tool before
    // answering sends two messages, and only the last is the answer.
    [Fact]
    public async Task TextBeforeAToolCall_IsNotReadAsPartOfTheAnswer()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("her usual toast"));
        var turn = await NextTurnAsync(agent);
        await SendAsync(agent, turn, ReplyContentType.Text, "Let me look at her foods.", "resp_1");
        await ReplyAsync(agent, turn, Estimate);

        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string, string> Unreadable => new()
    {
        { "prose", "That meal has about 40 g of carbs." },
        { "a field missing", Estimate.Replace("\"gramsHigh\":60,", "") },
        { "a total of its own", Estimate.Replace("\"notes\"", "\"total\":40,\"notes\"") }
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task AReplyOutsideTheSchema_AnswersTheAppWithAnError(string what, string reply)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        await ReplyAsync(agent, await NextTurnAsync(agent), reply);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway, what);
        (await DetailAsync(response)).ShouldContain("could not be read");
    }

    [Fact]
    public async Task ATurnThatEndsWithNoReply_AnswersTheAppWithAnError()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        await ReplyAsync(agent, await NextTurnAsync(agent));

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldContain("no answer");
    }

    // What failed is what she reads: an out-of-credit provider is worth knowing about, and is not
    // worth waiting on.
    [Fact]
    public async Task AnErrorOnTheTurn_IsTheAppsError_EvenAfterPartOfAnAnswer()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        var turn = await NextTurnAsync(agent);
        await SendAsync(agent, turn, ReplyContentType.Text, Estimate[..60]);
        await SendAsync(agent, turn, ReplyContentType.Error, "Insufficient credits");
        await SendAsync(agent, turn, ReplyContentType.StreamComplete, "", isComplete: true);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldBe("Insufficient credits");
    }

    [Fact]
    public async Task WithNoAgentListening_TheAppIsToldAtOnce()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("paella"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldContain("not connected");
    }

    // The app never waits on a turn without end: past the server's wait it is told so, and the turn
    // is cancelled rather than left running for an answer nobody will read.
    [Fact]
    public async Task ATurnThatOutlivesTheWait_IsCancelled_AndTheAppToldItTookTooLong()
    {
        var clock = new ArmedClock();
        await using var server = await SugaryServer.StartAsync(turnClock: clock);
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        var turn = await NextTurnAsync(agent);
        await clock.AdvancePastAsync(TimeSpan.FromSeconds(server.Settings.TurnTimeoutSeconds));

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await DetailAsync(response)).ShouldContain("too long");

        var cancel = await NextTurnAsync(agent);
        cancel.ConversationId.ShouldBe(turn.ConversationId);
        cancel.Sender.ShouldBe(ChannelProtocol.SystemSender);
    }

    // Photos reach the estimate in a later ticket; until then one is refused with a reason rather
    // than dropped, which would estimate the meal from words she did not mean to be the whole of it.
    [Fact]
    public async Task APhoto_IsRefusedRatherThanIgnored()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();
        var form = Form("paella");
        form.Add(new ByteArrayContent([1, 2, 3]), "photos", "meal-0.jpg");

        var response = await app.PostAsync("/api/estimates", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldContain("photo");
    }

    [Fact]
    public async Task NeitherPhotoNorDescription_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form(" "));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldContain("describe the meal");
    }

    // The app's own form, field for field as Sugary.Client's api.estimate builds it.
    private static MultipartFormDataContent Form(string description, string language = "en") => new()
    {
        { new StringContent(description), "description" },
        { new StringContent(""), "clarifications" },
        { new StringContent(""), "counted" },
        { new StringContent(language), "language" }
    };

    private static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(_wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

    // The agent's text in the chunks given, then the end of its turn.
    private static async Task ReplyAsync(McpChannelConnection agent, ChannelMessage turn, params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await SendAsync(agent, turn, ReplyContentType.Text, chunk);
        }

        await SendAsync(agent, turn, ReplyContentType.StreamComplete, "", isComplete: true);
    }

    private static Task SendAsync(
        McpChannelConnection agent, ChannelMessage turn, ReplyContentType type, string content,
        string messageId = "resp_2", bool isComplete = false) =>
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

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>();

    private static void ShouldBeInterval(JsonNode? interval, double low, double best, double high) =>
        new[] { "low", "best", "high" }.Select(bound => interval![bound]!.GetValue<double>())
            .ShouldBe([low, best, high]);
}