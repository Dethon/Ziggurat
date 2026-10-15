using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Prompts;
using Domain.Tools.Sugary.Estimation;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.Time.Testing;
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
        // A meal runs on the agent's own model; only a label switches.
        turn.ConfigPatch.ShouldBeNull();

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
        { "a total of its own", Estimate.Replace("\"notes\"", "\"total\":40,\"notes\"") },
        // Dropped, its carbs would go with it and the total would read low with nothing to show why.
        { "an item with carbs and no name", Estimate.Replace("\"name\":\"orange juice\"", "\"name\":\" \"") }
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
        // No English for her Spanish screen: the app words it, the reason goes to the log.
        (await DetailAsync(response)).ShouldBeNullOrEmpty(what);
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

    // The agent's error is the exception it caught, in English and often carrying the provider's
    // own body, so it goes to the server's log and never to her screen: the app is answered with no
    // detail and says it went wrong in her language. Still at once, since it is not worth waiting on.
    [Fact]
    public async Task AnErrorOnTheTurn_IsTheAppsError_EvenAfterPartOfAnAnswer_WithoutTheAgentsWords()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella"));
        var turn = await NextTurnAsync(agent);
        await SendAsync(agent, turn, ReplyContentType.Text, Estimate[..60]);
        await SendAsync(agent, turn, ReplyContentType.Error, ProviderError);
        await SendAsync(agent, turn, ReplyContentType.StreamComplete, "", isComplete: true);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldBeEmpty();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("credits");
    }

    // What the agent sends when its provider refuses the call: the exception's message as caught.
    private const string ProviderError =
        "An error occurred: Response status code does not indicate success: 402 (Payment Required). " +
        "{\"error\":{\"message\":\"Insufficient credits\",\"code\":402}}";

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

    // Every estimate is read back in WebChat, under the sugary agent. The channel asks for that on
    // each turn and names the topic; the conversation it opens is spelled as WebChat spells one, so a
    // topic can be shown over it and read the history the agent writes there (ADR 0046).
    [Fact]
    public async Task AnEstimateTurn_AsksForAReviewCopy_OnAConversationWebChatCanShow()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("two slices of toast and a glass of orange juice"));
        var turn = await NextTurnAsync(agent);

        turn.ReviewCopy.ShouldNotBeNull().Title.ShouldBe("Meal: two slices of toast and a glass of orange juice");
        ConversationIdentity.Parse(turn.ConversationId).ShouldNotBeNull();

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A photo with no words has nothing to name the meal by but that it was photographed. Long words
    // are cut, so the sidebar stays a line.
    [Theory]
    [InlineData("", "Meal photo")]
    [InlineData("a very long description of a mixed plate with rice, chicken and a salad on the side", "Meal: a very long description of a mixed plate with…")]
    public async Task TheReviewTopic_IsNamedAfterHerWords(string description, string title)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form(description, photos: [Photo(1, 2_000)]));
        var turn = await NextTurnAsync(agent);

        turn.ReviewCopy.ShouldNotBeNull().Title.ShouldBe(title);

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The photos are kept under the directory WebChat lays the same conversation out by, so the copy
    // WebChat keeps for the review sits where deleting the topic removes its files.
    [Fact]
    public async Task APhotosReference_IsFiledUnderTheConversationAsWebChatFilesIt()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", photos: [Photo(1, 2_000)]));
        var turn = await NextTurnAsync(agent);

        turn.Attachments.ShouldHaveSingleItem().Id
            .ShouldStartWith($"{AttachmentEndpointPaths.ConversationDirectory(turn.ConversationId)}/");

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
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

    // Her answers go back on the conversation that asked the questions, as the next turn of it: the
    // agent estimates again with its first message — her photo, her words — still in its history,
    // so the photo is not sent again and the turn does not claim there is none. What she counted
    // herself travels with this turn too, because she may have added it after the first estimate.
    [Fact]
    public async Task HerAnswers_AreTheNextTurnOfTheSameConversation_WithWhatSheCountedAndNoPhotoSentAgain()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var photo = Photo(1, 2_000);

        var first = app.PostAsync("/api/estimates", Form("", photos: [photo], counted: Bread));
        var firstTurn = await NextTurnAsync(agent);
        firstTurn.Content.ShouldContain($"Already added by the person and counted, so leave these out of your list:\n{Bread}");
        await ReplyAsync(agent, firstTurn, Estimate);
        var firstEstimate = await ReadAsync(await first);
        var conversation = firstEstimate["conversationId"]!.GetValue<string>();
        conversation.ShouldBe(firstTurn.ConversationId);

        var second = app.PostAsync("/api/estimates", FollowUp(conversation, Answer, $"{Bread}\n{Yoghurt}", "es"));
        var followUp = await NextTurnAsync(agent);

        followUp.ConversationId.ShouldBe(firstTurn.ConversationId);
        followUp.TurnKey.ShouldNotBe(firstTurn.TurnKey);
        followUp.ResponseFormat.ShouldNotBeNull().Name.ShouldBe(MealEstimateSchema.Name);
        followUp.Content.ShouldContain($"Their answers to your earlier questions:\n{Answer}");
        followUp.Content.ShouldContain(
            $"Already added by the person and counted, so leave these out of your list:\n{Bread}\n{Yoghurt}");
        followUp.Content.ShouldEndWith("Write every name, portion, note and question in Spanish.");
        followUp.Content.ShouldNotContain("photo");
        followUp.Attachments.ShouldBeNull();
        // The photo stays where the first turn left it, for hydration to put back from the history.
        (await FetchAsync(agent, firstTurn.Attachments.ShouldHaveSingleItem())).ShouldBe(photo);

        await ReplyAsync(agent, followUp, AnsweredEstimate);
        var secondEstimate = await ReadAsync(await second);
        secondEstimate["conversationId"]!.GetValue<string>().ShouldBe(conversation);
        secondEstimate["questions"]!.AsArray().ShouldBeEmpty();
        ShouldBeInterval(secondEstimate["total"], 45, 57, 75);
    }

    // Her answers are read back in WebChat beside the estimate they answer: the id the app carries
    // back is WebChat's spelling of the conversation, colon and all, and comes back unchanged, and
    // each answer asks for the review under the first estimate's title, since it has no words of the
    // meal to name it by and the topic is minted by whichever turn reaches WebChat first.
    [Fact]
    public async Task HerAnswers_AskForTheSameReviewCopy_OnTheConversationTheAppCarriedBack()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var first = app.PostAsync("/api/estimates", Form("a bowl of lentils"));
        var firstTurn = await NextTurnAsync(agent);
        await ReplyAsync(agent, firstTurn, Estimate);
        var conversation = (await ReadAsync(await first))["conversationId"]!.GetValue<string>();
        ConversationIdentity.Parse(conversation).ShouldNotBeNull().ConversationId.ShouldBe(firstTurn.ConversationId);

        var second = app.PostAsync("/api/estimates", FollowUp(conversation, Answer));
        var followUp = await NextTurnAsync(agent);

        followUp.ConversationId.ShouldBe(firstTurn.ConversationId);
        followUp.ReviewCopy.ShouldNotBeNull().Title.ShouldBe("Meal: a bowl of lentils");

        await ReplyAsync(agent, followUp, AnsweredEstimate);
        (await ReadAsync(await second))["conversationId"]!.GetValue<string>().ShouldBe(conversation);
    }

    // A conversation the server never opened is not one the agent can carry on: it would estimate
    // from her answers alone, with no photo and no words. Refused before anything is sent — there
    // is no agent even connected here — with a status the app reads as "start this meal again".
    [Theory]
    [InlineData("123:456")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("../sugary.db")]
    public async Task AnAnswerToAConversationTheServerNeverOpened_IsRefusedAsGone_BeforeAnythingIsSent(string conversation)
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", FollowUp(conversation, Answer));

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await DetailAsync(response)).ShouldContain("estimated again");
    }

    // A meal is answered at the table, minutes after its first estimate. A draft found again the next
    // day is a meal long eaten, and its conversation is closed rather than kept for ever.
    [Fact]
    public async Task AnAnswerPastTheWindow_IsRefusedAsGone_BeforeAnythingIsSent()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await SugaryServer.StartAsync(estimateClock: clock);
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var conversation = await EstimateAsync(app, agent);

        clock.Advance(TimeSpan.FromHours(server.Settings.FollowUpHours));
        var response = await app.PostAsync("/api/estimates", FollowUp(conversation, Answer));

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await DetailAsync(response)).ShouldContain("estimated again");
    }

    // The window runs from her last answer, not from the first estimate.
    [Fact]
    public async Task EachAnswer_KeepsTheConversationOpenForAnotherWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await SugaryServer.StartAsync(estimateClock: clock);
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var conversation = await EstimateAsync(app, agent);
        var almost = TimeSpan.FromHours(server.Settings.FollowUpHours) - TimeSpan.FromMinutes(1);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            clock.Advance(almost);
            var answer = app.PostAsync("/api/estimates", FollowUp(conversation, Answer));
            var turn = await NextTurnAsync(agent);
            turn.ConversationId.ShouldBe(conversation);
            await ReplyAsync(agent, turn, AnsweredEstimate);
            (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    // An amount she picked from the options is her rough judgement, so the model keeps it at medium
    // and its range is widened like any judged by eye; an amount she typed is a fact, marked high and
    // kept as given. Both reach the turn as she said them, and the prompt says how to read each and
    // not to ask again what she answered.
    [Fact]
    public async Task AfterHerAnswers_APickedAmountKeepsItsRange_AndATypedOneIsKeptAsGiven()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var conversation = await EstimateAsync(app, agent);
        const string typed = "Correction: \"white toast\" weighs 80 g.";

        var answer = app.PostAsync("/api/estimates", FollowUp(conversation, $"{Answer}\n{typed}"));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldContain($"Their answers to your earlier questions:\n{Answer}\n{typed}");
        SugaryPrompt.Prompt.ShouldContain("An amount they picked from options you offered is their rough judgement");
        SugaryPrompt.Prompt.ShouldContain("Stated weights, counts and answers about what a food is are facts");
        SugaryPrompt.Prompt.ShouldContain("never repeat a question that has already been answered");

        await ReplyAsync(agent, turn, AnsweredEstimate.Replace(
            "\"gramsLow\":60,\"grams\":60,\"gramsHigh\":60", "\"gramsLow\":80,\"grams\":80,\"gramsHigh\":80"));
        var items = (await ReadAsync(await answer))["items"]!.AsArray();

        ShouldBeInterval(items[0]!["grams"], 80, 80, 80);
        items[0]!["confidence"]!.GetValue<string>().ShouldBe("high");
        ShouldBeInterval(items[1]!["grams"], 180, 300, 480);
        items[1]!["confidence"]!.GetValue<string>().ShouldBe("medium");
    }

    // What she counted herself never comes back from the server: it is only text on the turn, and
    // the app keeps her items apart from the model's. When the model lists one of them anyway, the
    // server does not try to recognise it — her names and the model's rarely match word for word,
    // across two languages, and a food dropped on a wrong match would vanish from the meal unseen.
    // It comes back as an estimated item like any other, on the screen where she can remove it.
    [Fact]
    public async Task AFoodSheCountedThatTheModelListsAgain_ComesBackAsAnEstimatedItem_NeverDroppedUnseen()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("toast and juice", counted: Bread));
        var turn = await NextTurnAsync(agent);
        turn.Content.ShouldContain(Bread);
        await ReplyAsync(agent, turn, Estimate.Replace(
            "\"items\":[",
            "\"items\":[{\"name\":\"Pan de molde\",\"portion\":\"2 slices\",\"gramsLow\":64,\"grams\":64,\"gramsHigh\":64,\"carbsPer100g\":46,\"confidence\":\"high\",\"note\":null},"));

        var estimate = await ReadAsync(await answer);
        estimate["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>())
            .ShouldBe(["Pan de molde", "white toast", "orange juice"]);
        ShouldBeInterval(estimate["total"], 68.4, 76.4, 88.4);
    }

    // The first estimate is what the model is judged by later, so the meal she saves after answering
    // carries the first answer's total, not the one her answers produced. The app keeps it from the
    // first reply; the log stores and serves it as sent.
    [Fact]
    public async Task AMealSavedAfterHerAnswers_KeepsItsFirstEstimate()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var first = app.PostAsync("/api/estimates", Form("toast and juice", counted: Yoghurt));
        await ReplyAsync(agent, await NextTurnAsync(agent), Estimate);
        var firstEstimate = await ReadAsync(await first);
        var second = app.PostAsync("/api/estimates", FollowUp(
            firstEstimate["conversationId"]!.GetValue<string>(), Answer, Yoghurt));
        await ReplyAsync(agent, await NextTurnAsync(agent), AnsweredEstimate);
        var answered = await ReadAsync(await second);

        var items = answered["items"]!.AsArray().Select(item => (JsonNode)new JsonObject
        {
            ["name"] = item!["name"]!.GetValue<string>(),
            ["portion"] = item["portion"]!.GetValue<string>(),
            ["grams"] = item["grams"]!["best"]!.GetValue<double>(),
            ["carbsPer100g"] = item["carbsPer100g"]!.GetValue<double>(),
            ["carbsLow"] = item["carbs"]!["low"]!.GetValue<double>(),
            ["carbs"] = item["carbs"]!["best"]!.GetValue<double>(),
            ["carbsHigh"] = item["carbs"]!["high"]!.GetValue<double>(),
            ["source"] = "estimate"
        }).Append(new JsonObject
        {
            ["name"] = "yoghurt", ["carbsLow"] = 12, ["carbs"] = 12, ["carbsHigh"] = 12, ["source"] = "manual"
        });
        var saved = await app.PostAsJsonAsync("/api/meals", new JsonObject
        {
            ["eatenAt"] = "2026-10-15T13:30:00+02:00",
            ["outcome"] = "unknown",
            ["firstEstimate"] = firstEstimate["total"]!.DeepClone(),
            ["items"] = new JsonArray([.. items])
        });
        saved.StatusCode.ShouldBe(HttpStatusCode.Created);

        var meal = (await app.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().ShouldHaveSingleItem()!;
        ShouldBeInterval(meal["firstEstimate"], 39, 47, 59);
        meal["items"]!.AsArray().Sum(i => i!["carbs"]!.GetValue<double>()).ShouldBe(57 + 12);
        var row = (await server.ReadAsync("/meals/2026-10.csv")).Split('\n')[1].Split(',');
        row[2..9].ShouldBe(["69", "57", "87", "47", "39", "59", "unknown"]);
    }

    // A follow-up answers the estimate already made, whose photos the agent already has.
    [Fact]
    public async Task AnAnswerWithPhotos_IsRefused_AndNothingIsKept()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var conversation = await EstimateAsync(app, agent);
        var stored = StoredFiles(server);

        var response = await app.PostAsync(
            "/api/estimates", FollowUp(conversation, Answer, photos: [Photo(2, 10)]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldContain("already");
        StoredFiles(server).ShouldBe(stored);
    }

    // A refused answer is not a turn of the conversation, so it does not start the window again.
    [Fact]
    public async Task AnAnswerWithPhotos_DoesNotKeepTheConversationOpen()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await SugaryServer.StartAsync(estimateClock: clock);
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var conversation = await EstimateAsync(app, agent);
        var window = TimeSpan.FromHours(server.Settings.FollowUpHours);

        clock.Advance(window - TimeSpan.FromMinutes(1));
        (await app.PostAsync("/api/estimates", FollowUp(conversation, Answer, photos: [Photo(2, 10)])))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        clock.Advance(TimeSpan.FromMinutes(2));
        var response = await app.PostAsync("/api/estimates", FollowUp(conversation, Answer));

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    private const string Bread = "- Pan de molde, 64 g: 29 g of carbs";
    private const string Yoghurt = "- yoghurt: 12 g of carbs";
    private const string Answer = "Q: How big was the glass?\nA: large (300 ml)";

    // The juice re-estimated on her answer: a picked amount, so still medium and still widened.
    private const string AnsweredEstimate =
        """
        {"items":[
          {"name":"white toast","portion":"2 slices","gramsLow":60,"grams":60,"gramsHigh":60,"carbsPer100g":45,"confidence":"high","note":null},
          {"name":"orange juice","portion":"a large glass","gramsLow":250,"grams":300,"gramsHigh":350,"carbsPer100g":10,"confidence":"medium","note":null}
        ],
        "questions":[],
        "notes":[]}
        """;

    // A first estimate answered, and the conversation it opened.
    private static async Task<string> EstimateAsync(
        HttpClient app, McpChannelConnection agent, string reply = Estimate, IReadOnlyList<byte[]>? photos = null)
    {
        var answer = app.PostAsync("/api/estimates", Form("toast and juice", photos: photos ?? [Photo(1, 10)]));
        await ReplyAsync(agent, await NextTurnAsync(agent), reply);
        return (await ReadAsync(await answer))["conversationId"]!.GetValue<string>();
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    // The app's own form, field for field as Sugary.Client's api.estimate builds it: each photo a
    // `photos` part named meal-<n>.jpg, typed as the browser types the JPEG blob it shrank.
    private static MultipartFormDataContent Form(
        string description, string language = "en", IReadOnlyList<byte[]>? photos = null,
        string mediaType = "image/jpeg", string clarifications = "", string counted = "")
    {
        var form = new MultipartFormDataContent();
        foreach (var (photo, index) in (photos ?? []).Select((p, i) => (p, i)))
        {
            var part = new ByteArrayContent(photo);
            part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(part, "photos", $"meal-{index}.jpg");
        }

        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(clarifications), "clarifications");
        form.Add(new StringContent(counted), "counted");
        form.Add(new StringContent(language), "language");
        return form;
    }

    // The app's answers to an estimate, as Sugary.Client's estimateForm builds a follow-up: the
    // conversation it was given, her answers and corrections, what she counted herself, and nothing
    // of the first turn's photos or words.
    private static MultipartFormDataContent FollowUp(
        string conversation, string clarifications, string counted = "", string language = "en",
        IReadOnlyList<byte[]>? photos = null)
    {
        var form = Form("", language, photos, clarifications: clarifications, counted: counted);
        form.Add(new StringContent(conversation), "conversationId");
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