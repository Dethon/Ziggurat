using System.ClientModel;
using System.Text.Json.Nodes;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;

using Tests.Integration.Fixtures;

namespace Tests.Canary;

// One of the suite's two calls to a real paid API, kept so a change on OpenRouter's side — the
// Responses wire, tool calling, reasoning — fails a routine run before it fails the deployment.
// Everything else that talks to a model talks to FakeOpenRouter. It costs one cheap model's two calls
// and skips on a machine with no key; keep it that small.
[Trait("Category", "Canary")]
public class OpenRouterCanaryTests
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<OpenRouterCanaryTests>()
        .AddEnvironmentVariables()
        .Build();

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"] is { Length: > 0 } key
            ? key
            : throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["openRouter:reasoningModel"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    [SkippableFact]
    public async Task GetStreamingResponseAsync_ToolCallingPlusReasoning_DoesNot400()
    {
        var (apiUrl, apiKey, model) = GetConfig();

        using var client = new OpenRouterChatClient(apiUrl, apiKey, model);
        using var invoking = new FunctionInvokingChatClient(client)
        {
            IncludeDetailedErrors = true,
            MaximumIterationsPerRequest = 5
        };

        var called = 0;
        var tool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref called); return "ok"; }, "TestTool", "Returns ok");
        var options = new ChatOptions
        {
            Tools = [tool],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning"] = new JsonObject { ["effort"] = "low" }
            }
        };

        try
        {
            // Whether the model chose to call the tool and then to say something is the model's turn, not
            // the API's shape, so a turn that did neither is asked again before it reddens a routine run.
            var updates = await LlmAttempt.UntilAsync(
                () => LlmAttempt.WithinAsync(TimeSpan.FromSeconds(60), async ct =>
                {
                    var collected = new List<ChatResponseUpdate>();
                    await foreach (var update in invoking.GetStreamingResponseAsync(
                                       [new ChatMessage(ChatRole.User, "Call TestTool, then say DONE")],
                                       options,
                                       ct))
                    {
                        collected.Add(update);
                    }

                    return collected;
                }),
                collected => called > 0 && !string.IsNullOrWhiteSpace(string.Concat(collected.Select(u => u.Text))));

            updates.ShouldNotBeEmpty();
            called.ShouldBeGreaterThan(0, "the model never called the tool, so the tool round trip was not exercised");
            string.Concat(updates.Select(u => u.Text)).ShouldNotBeNullOrWhiteSpace();
        }
        catch (ClientResultException ex)
        {
            var raw = ex.GetRawResponse();
            var body = raw?.Content.ToString();
            throw new Exception($"OpenRouter returned {raw?.Status} {raw?.ReasonPhrase}. Body: {body}", ex);
        }
    }
}