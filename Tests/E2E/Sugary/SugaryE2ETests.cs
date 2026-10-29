using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Conversations;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.E2E.WebChat;

namespace Tests.E2E.Sugary;

// Sugary end to end on the stack: her app sends a meal photo, the real sugary agent estimates it as a
// turn of the Sugary channel, she saves the meal, the meal is in the table another agent reads under
// /sugary, and the conversation is a topic WebChat shows under the sugary agent.
//
// This reaches a live model (the shipped sugary agent's, Gemini 3.8 Flash today, through OpenRouter) and
// so spends money — about a cent. Run it on purpose, never as part of a routine run:
//   dotnet test --filter "Category=E2E&FullyQualifiedName~SugaryE2ETests"
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryE2ETests(WebChatE2EFixture fixture)
{
    // Nutrition5k dish 1568061145 (a slice of cheese pizza with squash), overhead RGB image, from
    // google-research-datasets/Nutrition5k, CC BY 4.0. One of the Sugary benchmark's photos
    // (Tests/Eval/Sugary/README.md), committed alone because it is small and its licence allows it.
    private static readonly string _photoPath =
        Path.Combine(TestHelpers.FindSolutionRoot(), "Tests", "E2E", "Sugary", "meal-photo.jpg");

    private static readonly TimeZoneInfo _home = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    [SkippableFact]
    public async Task AMealPhotoFromTheApp_IsEstimatedByTheSugaryAgent_SavedToHerLog_ReadAtSugary_AndShownInWebChat()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        // WebChat's topic row is found by its text, so the run names what it makes: early in her words,
        // because the review topic is titled with them, cut at 48 characters.
        var tag = Guid.NewGuid().ToString("N")[..4];
        var words = $"E2E {tag}: a slice of cheese pizza with roasted squash";

        using var app = await SignedInAsync();

        // The estimate, as the app's api.estimate sends it.
        var estimate = await EstimateAsync(app, words);

        var items = estimate["items"].ShouldBeOfType<JsonArray>();
        items.ShouldNotBeEmpty("a plate of pizza and squash was estimated as no food at all");
        estimate["total"]!["best"]!.GetValue<double>().ShouldBeGreaterThan(0, "pizza was counted as no carbs");
        var conversationId = estimate["conversationId"]!.GetValue<string>();
        ConversationIdentity.Parse(conversationId).ShouldNotBeNull(
            "the conversation must be spelled as WebChat spells one, or no review topic can show it");

        // Saved as the app saves a meal: its items as estimated, the first estimate kept beside them.
        var eatenAt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _home);
        var saved = await app.PostAsJsonAsync("/api/meals", new JsonObject
        {
            ["eatenAt"] = eatenAt.ToString("O"),
            ["notes"] = words,
            ["outcome"] = "unknown",
            ["firstEstimate"] = estimate["total"]!.DeepClone(),
            ["items"] = new JsonArray([.. items.Select(item => (JsonNode)new JsonObject
            {
                ["name"] = item!["name"]!.GetValue<string>(),
                ["portion"] = item["portion"]!.GetValue<string>(),
                ["grams"] = item["grams"]!["best"]!.GetValue<double>(),
                ["carbsPer100g"] = item["carbsPer100g"]!.GetValue<double>(),
                ["carbsLow"] = item["carbs"]!["low"]!.GetValue<double>(),
                ["carbs"] = item["carbs"]!["best"]!.GetValue<double>(),
                ["carbsHigh"] = item["carbs"]!["high"]!.GetValue<double>(),
                ["source"] = "estimate"
            })])
        });
        saved.StatusCode.ShouldBe(HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var mealId = (await saved.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        // Asked as an agent with the mount asks it: the server's query tool over MCP behind the
        // deployment secret, the statement the agent's own query tool would send.
        var rows = await QueryMountAsync($"select notes from meals where meal_id = '{mealId}'");
        rows.Count.ShouldBe(1, $"meal {mealId} is not in /sugary's meals: {rows.ToJsonString()}");
        rows[0]![0]!.GetValue<string>().ShouldContain(tag);

        await TheReviewTopicShowsTheEstimateAsync(tag);
    }

    private async Task<HttpClient> SignedInAsync()
    {
        // Past the server's own cap on the turn, so it is the server's 504 that says the wait ran out.
        var app = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(fixture.SugaryUrl),
            Timeout = WebChatStack.SugaryTurnTimeout + TimeSpan.FromMinutes(1)
        };
        var signIn = await app.PostAsJsonAsync("/api/session", new { code = WebChatStack.SugaryAccessCode });
        signIn.StatusCode.ShouldBe(HttpStatusCode.NoContent, await signIn.Content.ReadAsStringAsync());
        return app;
    }

    private static async Task<JsonNode> EstimateAsync(HttpClient app, string words)
    {
        var photo = await File.ReadAllBytesAsync(_photoPath);

        // The agent's channel connection reaches the server on its own schedule after the stack is
        // up, and the server answers "not connected" at once until it has — no turn is sent and
        // nothing is spent — so that answer alone is asked again, for a bounded while.
        var connectDeadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await app.PostAsync("/api/estimates", Form(photo, words));
            }
            catch (TaskCanceledException ex)
            {
                throw ModelTimeout(ex);
            }

            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                return JsonNode.Parse(body)!;
            }

            if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                throw ModelTimeout(null);
            }

            if (response.StatusCode == HttpStatusCode.BadGateway
                && body.Contains("not connected", StringComparison.Ordinal)
                && DateTime.UtcNow < connectDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            throw new ShouldAssertException(
                $"The estimate failed with {(int)response.StatusCode}: {body}. A 502 carrying a reason is the "
                + "agent's turn failing (the provider refused it, or the answer was outside the schema).");
        }
    }

    // The estimate is a real model's answer over the network, so the provider's worst case is what
    // bounds it, as it bounds the chat E2E's reply; when that cap is passed the failure says so, or
    // a slow generation reads as the Sugary channel losing the answer.
    private static TimeoutException ModelTimeout(Exception? inner) => new(
        $"No estimate after {WebChatStack.SugaryTurnTimeout.TotalMinutes:0} minutes. The estimate comes from a "
        + "real model here (the sugary agent's, through OpenRouter), so this is a slow or failed generation "
        + "rather than the Sugary channel or the app.", inner);

    private static MultipartFormDataContent Form(byte[] photo, string words)
    {
        var picture = new ByteArrayContent(photo);
        picture.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent
        {
            { picture, "photos", "meal-0.jpg" },
            { new StringContent(words), "description" },
            { new StringContent(""), "clarifications" },
            { new StringContent(""), "counted" },
            { new StringContent("en"), "language" }
        };
    }

    private async Task<JsonArray> QueryMountAsync(string sql)
    {
        await using var mcp = await McpClient.CreateAsync(McpTestSecret.Transport(fixture.SugaryMcpEndpoint, McpTestSecret.For("mcp-sugary")));
        var result = await mcp.CallToolAsync("fs_query", new Dictionary<string, object?> { ["path"] = "/", ["sql"] = sql });
        var answer = JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))!;
        return answer["rows"]?.AsArray()
               ?? throw new ShouldAssertException($"querying /sugary over MCP failed: {answer.ToJsonString()}");
    }

    // The review copy is minted in WebChat's default space (another channel's conversation carries no
    // space of WebChat's), under the sugary agent, titled with her words. Her photo is copied into
    // WebChat's store in the background, so it is waited for rather than expected at once.
    private async Task TheReviewTopicShowsTheEstimateAsync(string tag)
    {
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.DefaultSpaceUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        await WebChatE2ETests.ClickThroughApprovalsAsync(
            page, page.Locator(".agent-seg", new PageLocatorOptions { HasText = "Sugary" }));

        var topic = page.Locator(".topic-item", new PageLocatorOptions { HasText = tag });
        await topic.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, topic);

        await Assertions.Expect(page.Locator(".chat-message.user .message-content").First)
            .ToContainTextAsync(tag, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await page.Locator(".chat-message.user .message-attachments").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
            .ToContainTextAsync("items", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }
}