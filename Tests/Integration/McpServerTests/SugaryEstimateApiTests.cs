using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Estimation;
using Infrastructure.Agents;
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

    // A photo with no words, as she takes most of them. The turn carries a reference, never the
    // bytes, and the agent gets the bytes back the way it does for every channel: asking the channel
    // through its own attachment source, which is what hydration calls on the way to the model.
    [Fact]
    public async Task OnePhoto_ReachesTheTurnAsAReference_AndItsBytesAreFetchedAsTheAgentFetchesThem()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var photo = Photo(1, 2_000);

        var answer = app.PostAsync("/api/estimates", Form("", photos: [photo]));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldStartWith("One photo of the meal is attached.");
        turn.Content.ShouldNotContain("The person's description");
        var reference = turn.Attachments.ShouldHaveSingleItem();
        reference.MediaType.ShouldBe("image/jpeg");
        reference.SizeBytes.ShouldBe(photo.Length);
        reference.Stills.ShouldBeNull();
        // The history keeps what the turn carries, so what it carries must not be the photo.
        turn.Content.ShouldNotContain(Convert.ToBase64String(photo)[..40]);

        (await FetchAsync(agent, reference)).ShouldBe(photo);

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Several photos of one plate, with her words beside them: each a reference of its own, each
    // fetched back as what was sent, and the text says how many there are.
    [Fact]
    public async Task SeveralPhotosOfOneMeal_EachReachTheTurn_AndTheTextSaysHowMany()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        byte[][] photos = [Photo(1, 3_000), Photo(2, 4_000), Photo(3, 5_000)];

        var answer = app.PostAsync("/api/estimates", Form("the sauce is cream", photos: photos));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldStartWith("3 photos of the same meal are attached.");
        turn.Content.ShouldContain("The person's description:\nthe sauce is cream");
        turn.Attachments.ShouldNotBeNull();
        turn.Attachments.Count.ShouldBe(3);
        turn.Attachments.Select(a => a.Id).Distinct().Count().ShouldBe(3);
        for (var i = 0; i < photos.Length; i++)
        {
            (await FetchAsync(agent, turn.Attachments[i])).ShouldBe(photos[i]);
        }

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A reference the store does not hold — swept, or never one of its own — reads back as nothing,
    // which hydration turns into a placeholder naming the file. Never another file, never an error.
    [Theory]
    [InlineData("nobody/nothing")]
    [InlineData("../sugary.db")]
    [InlineData("..")]
    public async Task AReferenceTheStoreDoesNotHold_IsFetchedAsNothing(string id)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();

        (await agent.FetchAttachmentAsync(id, CancellationToken.None)).ShouldBeNull();
    }

    // Refused before anything is sent, with no agent even connected: a refusal that came after the
    // send would answer "not connected" instead. Nothing is kept of a photo that was refused.
    [Fact]
    public async Task APhotoTooLargeForTheModel_IsRefusedBeforeItIsSent_WithAReasonTheAppShows()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();
        var tooLarge = Photo(1, (int)server.Settings.Uploads.MaxPhotoBytes + 1);

        var response = await app.PostAsync("/api/estimates", Form("paella", photos: [Photo(2, 10), tooLarge]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldBe("Each photo must be under 5 MB, the most the estimating models take.");
        StoredFiles(server).ShouldBeEmpty();
    }

    public static TheoryData<string, IReadOnlyList<byte[]>, string, string> RefusedPhotos => new()
    {
        { "not a picture", [Photo(1, 10)], "image/heic", "Photos must be JPEG, PNG or WebP." },
        { "empty", [[]], "image/jpeg", "A photo arrived empty. Take it again." },
        { "five of one meal", [.. Enumerable.Range(1, 5).Select(i => Photo((byte)i, 10))], "image/jpeg",
            "At most 4 photos per meal." }
    };

    [Theory]
    [MemberData(nameof(RefusedPhotos))]
    public async Task PhotosTheEstimateCannotTake_AreRefusedBeforeTheyAreSent(
        string what, IReadOnlyList<byte[]> photos, string mediaType, string reason)
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("paella", photos: photos, mediaType: mediaType));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, what);
        (await DetailAsync(response)).ShouldBe(reason, what);
        StoredFiles(server).ShouldBeEmpty(what);
    }

    // A model that cannot see pictures would answer from her words alone, or from nothing, as if no
    // photo had been sent. The channel learns what the sugary agent's model takes from the catalogue
    // the agent registers on connect, and refuses the turn before it is sent; words alone still go.
    [Fact]
    public async Task AModelWithoutImageInput_HasAPhotoTurnRefused_NeverAnsweredAsIfNoPhotoWasSent()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync([Sugary("text/only", accepts: [])], CancellationToken.None);
        var app = await server.SignedInAsync();

        var refused = await app.PostAsync("/api/estimates", Form("paella", photos: [Photo(1, 10)]));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(refused)).ShouldBe(
            "text/only cannot read photos, so this meal cannot be estimated from them. " +
            "Describe it in words instead, or count it by hand.");
        StoredFiles(server).ShouldBeEmpty();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        var turn = await NextTurnAsync(agent);
        turn.Content.ShouldContain("paella");
        turn.Attachments.ShouldBeNull();
        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The catalogue names every agent, and only the one the app's turns are routed to is asked about.
    // Where it is silent — nothing registered yet, or no entry for that agent — photos go, as every
    // channel's capability check is permissive while discovery has not answered.
    public static TheoryData<string, AgentCatalogEntry[]> PhotosGo => new()
    {
        { "its model sees pictures", [Sugary("sees/pictures", accepts: [AttachmentKind.Image])] },
        { "only another agent's model is blind",
            [new AgentCatalogEntry("jonas", "Jonas", null, "text/only", DefaultModelAttachmentKinds: [])] },
        { "nothing registered", [] }
    };

    [Theory]
    [MemberData(nameof(PhotosGo))]
    public async Task APhotoTurn_GoesWhereTheCatalogueDoesNotSayTheModelIsBlind(
        string what, AgentCatalogEntry[] catalogue)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync(catalogue, CancellationToken.None);
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", photos: [Photo(1, 10)]));
        var turn = await NextTurnAsync(agent);

        turn.Attachments.ShouldNotBeNull(what).Count.ShouldBe(1, what);
        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK, what);
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

    // The app's own form, field for field as Sugary.Client's api.estimate builds it: each photo a
    // `photos` part named meal-<n>.jpg, typed as the browser types the JPEG blob it shrank.
    private static MultipartFormDataContent Form(
        string description, string language = "en", IReadOnlyList<byte[]>? photos = null,
        string mediaType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        foreach (var (photo, index) in (photos ?? []).Select((p, i) => (p, i)))
        {
            var part = new ByteArrayContent(photo);
            part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(part, "photos", $"meal-{index}.jpg");
        }

        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(""), "clarifications");
        form.Add(new StringContent(""), "counted");
        form.Add(new StringContent(language), "language");
        return form;
    }

    // Bytes no other photo in the test shares, so a fetch that answered with the wrong one fails.
    private static byte[] Photo(byte seed, int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed))];

    // The sugary agent as the agent host registers it: its own model, and what that model accepts.
    private static AgentCatalogEntry Sugary(string model, IReadOnlyList<AttachmentKind> accepts) =>
        new("sugary", "Sugary", null, model, DefaultModelAttachmentKinds: accepts);

    private static IReadOnlyList<string> StoredFiles(SugaryServer server) =>
        Directory.Exists(server.Settings.Uploads.StoragePath)
            ? Directory.GetFiles(server.Settings.Uploads.StoragePath, "*", SearchOption.AllDirectories)
            : [];

    // The agent's own IAttachmentSource over its own connection to this channel: what
    // AttachmentHydration asks when the turn goes to the model.
    private static Task<byte[]?> FetchAsync(McpChannelConnection agent, AttachmentReference reference) =>
        new ChannelAttachmentSource([agent]).FetchAsync("sugary", reference.Id, CancellationToken.None);

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