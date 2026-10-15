using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Tools.Sugary.Labels;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A nutrition label photographed in the app, read by the sugary agent on the label model: the app
// posts the photo as it always did, the agent receives a turn asking for the label's format and
// switched to the stronger model, and the app gets the printed values back — or the reason they
// could not be read. The agent is played over MCP by its own channel connection.
public class SugaryLabelApiTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private const string Galletas =
        """{"readable":true,"carbsPer100g":61.5,"unit":"g","servingGrams":25,"name":"Galletas María","note":null}""";

    [Fact]
    public async Task ALabelPhoto_IsATurnOnTheLabelModel_InTheLabelsFormat_AndItsValuesReachTheApp()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var photo = Photo(7, 3_000);

        var answer = app.PostAsync("/api/labels", Form(photo));
        var turn = await NextTurnAsync(agent);

        turn.ConfigPatch.ShouldNotBeNull().Model.ShouldBe("anthropic/claude-opus-5.5");
        turn.ConfigPatch.Model.ShouldBe(server.Settings.LabelModel);
        turn.ConfigPatch.ReasoningEffort.ShouldBeNull();
        turn.ResponseFormat.ShouldNotBeNull().Name.ShouldBe(NutritionLabelSchema.Name);
        JsonElement.DeepEquals(turn.ResponseFormat.Schema, NutritionLabelSchema.Format.Schema).ShouldBeTrue();
        turn.Content.ShouldBe(LabelTurn.Text("en"));
        turn.AgentId.ShouldBeNull();
        var reference = turn.Attachments.ShouldHaveSingleItem();
        reference.MediaType.ShouldBe("image/jpeg");
        (await FetchAsync(agent, reference)).ShouldBe(photo);

        await ReplyAsync(agent, turn, Galletas[..30], Galletas[30..]);

        var food = await ReadAsync(await answer);
        food["name"]!.GetValue<string>().ShouldBe("Galletas María");
        food["carbsPer100g"]!.GetValue<double>().ShouldBe(61.5);
        food["servingGrams"]!.GetValue<double>().ShouldBe(25);
        food["unit"]!.GetValue<string>().ShouldBe("g");
        food["note"].ShouldBeNull();
        food["foodId"].ShouldBeNull();
    }

    // A label is read back in WebChat as a meal is, so a misread can be traced to what the model was
    // shown: the turn asks for a review copy, on a conversation spelled as WebChat spells one, with
    // the photo filed where WebChat files that conversation (ADR 0046).
    [Fact]
    public async Task ALabelTurn_AsksForAReviewCopy_OnAConversationWebChatCanShow()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        var turn = await NextTurnAsync(agent);

        turn.ReviewCopy.ShouldNotBeNull().Title.ShouldBe("Nutrition label");
        ConversationIdentity.Parse(turn.ConversationId).ShouldNotBeNull();
        turn.Attachments.ShouldHaveSingleItem().Id
            .ShouldStartWith($"{AttachmentEndpointPaths.ConversationDirectory(turn.ConversationId)}/");

        await ReplyAsync(agent, turn, Galletas);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheAppsLanguage_ReachesTheLabelTurn()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10), "es"));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldEndWith("Write name and note in Spanish.");
        await ReplyAsync(agent, turn, Galletas);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A photo that is not a readable label is refused rather than guessed, with the model's reason in
    // her language, shown by the app as it came.
    [Theory]
    [InlineData("""{"readable":false,"carbsPer100g":null,"unit":"g","servingGrams":null,"name":null,"note":"Esto es un plato de paella, no una etiqueta."}""",
        "Esto es un plato de paella, no una etiqueta.")]
    [InlineData("""{"readable":true,"carbsPer100g":640,"unit":"g","servingGrams":null,"name":null,"note":"The carbohydrate row is cut off."}""",
        "The carbohydrate row is cut off.")]
    public async Task APhotoThatIsNotAReadableLabel_IsAnsweredUnreadable_WithTheReason(string reply, string reason)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10), "es"));
        await ReplyAsync(agent, await NextTurnAsync(agent), reply);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DetailAsync(response)).ShouldBe(reason);
    }

    // With no reason from the model the server words none either: an English sentence of its own would
    // reach her Spanish screen as it is. The detail is there and empty, which the app reads as "no
    // reason" and answers with its own "I couldn't read that label" in her language (errors.ts,
    // labelFailureMessage). Present rather than absent, because absent the app would fall back to the
    // problem's title, "Unprocessable Entity".
    [Theory]
    [InlineData("null")]
    [InlineData("\"  \"")]
    public async Task ARefusalWithNoReason_IsAnsweredUnreadable_WithAnEmptyDetail_ForTheAppToWordInHerLanguage(
        string note)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10), "es"));
        await ReplyAsync(agent, await NextTurnAsync(agent),
            $$"""{"readable":false,"carbsPer100g":null,"unit":"g","servingGrams":null,"name":null,"note":{{note}}}""");

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = (await response.Content.ReadFromJsonAsync<JsonNode>()).ShouldNotBeNull();
        problem["detail"].ShouldNotBeNull().GetValue<string>().ShouldBeEmpty();
        problem.ToJsonString().ShouldNotContain("could not be read");
    }

    public static TheoryData<string, string> Unreadable => new()
    {
        { "prose", "The label says 61.5 g of carbohydrate per 100 g." },
        { "a meal estimate", """{"items":[],"questions":[],"notes":[]}""" },
        { "a field missing", Galletas.Replace(""","note":null""", "") },
        { "a field it never asked for", Galletas.Replace("}", ""","insulin":"4 units"}""") }
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task AReplyOutsideTheLabelsSchema_AnswersTheAppWithAnError(string what, string reply)
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        await ReplyAsync(agent, await NextTurnAsync(agent), reply);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway, what);
        // No English for her Spanish screen: the app words it, the reason goes to the log.
        (await DetailAsync(response)).ShouldBeNullOrEmpty(what);
    }

    // The agent's error is the exception it caught, in English and often carrying the provider's
    // own body: logged here, and the app answered with no detail so it says so in her language.
    [Fact]
    public async Task AnErrorOnTheLabelTurn_IsTheAppsError_WithoutTheAgentsWords()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        var turn = await NextTurnAsync(agent);
        await SendAsync(agent, turn, ReplyContentType.Error,
            "An error occurred: Response status code does not indicate success: 402 (Payment Required). " +
            "{\"error\":{\"message\":\"Insufficient credits\",\"code\":402}}");
        await SendAsync(agent, turn, ReplyContentType.StreamComplete, "", isComplete: true);

        var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldBeEmpty();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("credits");
    }

    [Fact]
    public async Task WithNoAgentListening_TheAppIsToldAtOnce()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/labels", Form(Photo(1, 10)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldContain("not connected");
    }

    [Fact]
    public async Task NoPhoto_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var form = new MultipartFormDataContent { { new StringContent("en"), "language" } };
        var response = await app.PostAsync("/api/labels", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldBe("Attach a photo of the label.");
    }

    // The same rules as a meal's photos, before anything is kept or sent: no agent is even connected.
    [Theory]
    [InlineData("not a picture", "image/heic", 10, "Photos must be JPEG, PNG or WebP.")]
    [InlineData("empty", "image/jpeg", 0, "A photo arrived empty. Take it again.")]
    [InlineData("too large", "image/jpeg", 5 * 1024 * 1024 + 1, "Each photo must be under 5 MB, the most the estimating models take.")]
    public async Task ALabelPhotoTheModelCannotTake_IsRefusedBeforeItIsSent(
        string what, string mediaType, int length, string reason)
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/labels", Form(Photo(1, length), mediaType: mediaType));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, what);
        (await DetailAsync(response)).ShouldBe(reason, what);
        StoredFiles(server).ShouldBeEmpty(what);
    }

    // A label is read by the label model, so whether a photo can go is asked about that model and not
    // the agent's own: a label model that cannot see refuses the label even when the meal model can,
    // and the other way round a label still goes.
    [Fact]
    public async Task ALabelModelThatCannotSee_HasTheLabelRefused_WhileAMealPhotoStillGoes()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync(
            [Sugary(sees: [AttachmentKind.Image], labelModelSees: [], labelModel: server.Settings.LabelModel)],
            CancellationToken.None);
        var app = await server.SignedInAsync();

        var refused = await app.PostAsync("/api/labels", Form(Photo(1, 10)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(refused)).ShouldBe(
            "anthropic/claude-opus-5.5 cannot read photos, so this label cannot be read. Add the food by hand.");
        StoredFiles(server).ShouldBeEmpty();

        var meal = app.PostAsync("/api/estimates", MealForm(Photo(2, 10)));
        var turn = await NextTurnAsync(agent);
        turn.Attachments.ShouldNotBeNull().Count.ShouldBe(1);
        turn.ConfigPatch.ShouldBeNull();
        await ReplyAsync(agent, turn, """{"items":[],"questions":[],"notes":[]}""");
        (await meal).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ALabelModelThatSees_TakesTheLabel_EvenWhenTheMealModelCannot()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync(
            [Sugary(sees: [], labelModelSees: [AttachmentKind.Image], labelModel: server.Settings.LabelModel)],
            CancellationToken.None);
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        var turn = await NextTurnAsync(agent);

        turn.Attachments.ShouldNotBeNull().Count.ShouldBe(1);
        await ReplyAsync(agent, turn, Galletas);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The agent honours a patch only for a model on its patchable list, and otherwise runs its own
    // model with a warning in its log (McpAgentReasoningTests.RunStreaming_NonWhitelistedModelPatch_*).
    // For a label that would be a silent read on the cheaper model, shown as a printed value — the
    // misread the stronger model is there to prevent. So when the agent's registered catalogue says
    // the label model is not one it may switch to, the label is refused before anything is sent.
    [Fact]
    public async Task ALabelModelTheAgentMayNotSwitchTo_HasTheLabelRefused_BeforeAnythingIsSent()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync(
            [Sugary(sees: [AttachmentKind.Image], labelModelSees: [AttachmentKind.Image], labelModel: "other/model")],
            CancellationToken.None);
        var app = await server.SignedInAsync();

        // A turn sent would hold this request until the agent answered; none will.
        var response = await app.PostAsync("/api/labels", Form(Photo(1, 10)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await DetailAsync(response)).ShouldBe(
            "anthropic/claude-opus-5.5 is not a model the sugary agent may switch to, so this label cannot be read. " +
            "Add the food by hand.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // The label model may be the agent's own; then no switch is needed and the label goes.
    [Fact]
    public async Task ALabelModelThatIsTheAgentsOwn_TakesTheLabel()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        await agent.RegisterAgentsAsync(
            [new AgentCatalogEntry("sugary", "Sugary", null, server.Settings.LabelModel, PatchableModels: [])],
            CancellationToken.None);
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        await ReplyAsync(agent, await NextTurnAsync(agent), Galletas);

        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // What the app does with a read label when "Save to my foods" is ticked: her food, kept with the
    // label's numbers and her usual amount, marked as coming from a label, and listed for next time.
    [Fact]
    public async Task AReadLabel_SavedToHerFoods_IsKeptAsALabelFood()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/labels", Form(Photo(1, 10)));
        await ReplyAsync(agent, await NextTurnAsync(agent), Galletas);
        var label = await ReadAsync(await answer);

        var saved = await app.PostAsJsonAsync("/api/foods", new JsonObject
        {
            ["name"] = label["name"]!.GetValue<string>(),
            ["carbsPer100g"] = label["carbsPer100g"]!.GetValue<double>(),
            ["unit"] = label["unit"]!.GetValue<string>(),
            ["portionGrams"] = label["servingGrams"]!.GetValue<double>(),
            ["portionCarbs"] = null,
            ["barcode"] = null,
            ["source"] = "label"
        });
        saved.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await saved.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        var food = (await app.GetFromJsonAsync<JsonNode>("/api/foods"))!.AsArray().ShouldHaveSingleItem()!;
        food["name"]!.GetValue<string>().ShouldBe("Galletas María");
        food["carbsPer100g"]!.GetValue<double>().ShouldBe(61.5);
        food["portionGrams"]!.GetValue<double>().ShouldBe(25);
        food["source"]!.GetValue<string>().ShouldBe("label");
        (await server.ReadAsync("/foods.csv")).ShouldContain($"{id},Galletas María,61.5,g,25,,,label");
    }

    // The sugary agent as the agent host registers it: its own model and what it takes, and the label
    // model among the models a turn may switch to, with what that one takes.
    private static AgentCatalogEntry Sugary(
        IReadOnlyList<AttachmentKind> sees, IReadOnlyList<AttachmentKind> labelModelSees, string labelModel) =>
        new("sugary", "Sugary", null, "google/gemini-3.8-flash",
            PatchableModels:
            [
                new PatchableModel("anthropic/claude-haiku-5.5", "Haiku 5.5", [AttachmentKind.Image]),
                new PatchableModel(labelModel, "Label model", labelModelSees)
            ],
            DefaultModelAttachmentKinds: sees);

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    // The app's own form, as Sugary.Client's api.readLabel builds it: one `photo` part named
    // label.jpg, shrunk on the phone to a JPEG, and the app's language.
    private static MultipartFormDataContent Form(byte[] photo, string language = "en", string mediaType = "image/jpeg")
    {
        var part = new ByteArrayContent(photo);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new MultipartFormDataContent
        {
            { part, "photo", "label.jpg" },
            { new StringContent(language), "language" }
        };
    }

    private static MultipartFormDataContent MealForm(byte[] photo)
    {
        var part = new ByteArrayContent(photo);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent
        {
            { part, "photos", "meal-0.jpg" },
            { new StringContent(""), "description" },
            { new StringContent(""), "clarifications" },
            { new StringContent(""), "counted" },
            { new StringContent("en"), "language" }
        };
    }

    private static byte[] Photo(byte seed, int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed))];

    private static IReadOnlyList<string> StoredFiles(SugaryServer server) =>
        Directory.Exists(server.Settings.Uploads.StoragePath)
            ? Directory.GetFiles(server.Settings.Uploads.StoragePath, "*", SearchOption.AllDirectories)
            : [];

    private static Task<byte[]?> FetchAsync(McpChannelConnection agent, AttachmentReference reference) =>
        new ChannelAttachmentSource([agent]).FetchAsync("sugary", reference.Id, CancellationToken.None);

    private static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(_wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

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
        bool isComplete = false) =>
        agent.SendReplyAsync(new SendReplyParams
        {
            ConversationId = turn.ConversationId,
            Content = content,
            ContentType = type,
            IsComplete = isComplete,
            MessageId = "resp_1",
            TurnKey = turn.TurnKey,
            AgentInitiated = false
        }, CancellationToken.None);

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>();
}