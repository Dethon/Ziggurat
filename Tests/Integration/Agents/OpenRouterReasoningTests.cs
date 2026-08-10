using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;

using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class OpenRouterReasoningTests
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<OpenRouterReasoningTests>()
        .Build();

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["openRouter:reasoningModel"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    [SkippableFact]
    public async Task GetStreamingResponseAsync_WithOpenRouterReasoning_YieldsTextReasoningContent()
    {
        var (apiUrl, apiKey, model) = GetConfig();

        using var client = new OpenRouterChatClient(apiUrl, apiKey, model);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Low,
                Output = ReasoningOutput.Full
            }
        };

        var updates = await LlmAttempt.WithinAsync(LlmAttempt.Budget, async ct =>
        {
            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(
                               [
                                   new ChatMessage(ChatRole.User,
                                       "Solve 9.11 vs 9.9. Provide the answer. (Reasoning should be enabled.)")
                               ],
                               options,
                               ct))
            {
                collected.Add(update);
            }

            return collected;
        });

        var reasoning = string.Join("", updates
            .SelectMany(u => u.Contents)
            .OfType<TextReasoningContent>()
            .Select(r => r.Text));

        if (string.IsNullOrWhiteSpace(reasoning))
        {
            // Debug dump to help adjust extraction for provider-specific shapes.
            DumpRaw(updates.Take(5).ToArray());
        }

        reasoning.ShouldNotBeNullOrWhiteSpace();
    }

    private static void DumpRaw(ChatResponseUpdate[] updates)
    {
        var idx = 0;
        foreach (var update in updates)
        {
            idx++;
            Console.WriteLine($"--- Update #{idx} ---");
            Console.WriteLine($"Text: {update.Text}");
            Console.WriteLine($"FinishReason: {update.FinishReason}");

            var raw = update.RawRepresentation;
            if (raw is null)
            {
                Console.WriteLine("RawRepresentation: <null>");
                continue;
            }

            Console.WriteLine($"RawRepresentation type: {raw.GetType().FullName}");

            var choices = GetPropertyValue(raw, "Choices") as IEnumerable;
            if (choices is null)
            {
                Console.WriteLine("Choices: <null>");
                continue;
            }

            var choiceIndex = 0;
            foreach (var choice in choices)
            {
                choiceIndex++;
                Console.WriteLine($"Choice #{choiceIndex} type: {choice?.GetType().FullName}");
                if (choice is null)
                {
                    continue;
                }

                var deltaOrMessage = GetPropertyValue(choice, "Delta") ?? GetPropertyValue(choice, "Message");
                Console.WriteLine($"Delta/Message type: {deltaOrMessage?.GetType().FullName}");

                DumpInterestingMembers(choice, "choice");
                if (deltaOrMessage is not null)
                {
                    DumpInterestingMembers(deltaOrMessage, "delta/message");

                    var directReasoning = GetAnyMemberValue(deltaOrMessage,
                        "Reasoning", "ReasoningContent", "reasoning", "reasoning_content", "Thinking", "thinking");
                    if (directReasoning is not null)
                    {
                        Console.WriteLine($"Direct reasoning member: {FormatValue(directReasoning)}");
                    }

                    var additional = GetAnyMemberValue(deltaOrMessage,
                        "SerializedAdditionalRawData", "AdditionalRawData", "AdditionalProperties",
                        "_serializedAdditionalRawData", "_additionalRawData");

                    DumpAdditional(additional);
                }
            }
        }
    }

    private static object? GetPropertyValue(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return instance.GetType().GetProperty(name, flags)?.GetValue(instance);
    }

    private static object? GetFieldValue(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return instance.GetType().GetField(name, flags)?.GetValue(instance);
    }

    private static object? GetAnyMemberValue(object instance, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyValue(instance, name);
            if (prop is not null)
            {
                return prop;
            }

            var field = GetFieldValue(instance, name);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static void DumpInterestingMembers(object instance, string label)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();
        var props = type.GetProperties(flags)
            .Select(p => p.Name)
            .Where(n => n.Contains("reason", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("think", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("additional", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("raw", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        var fields = type.GetFields(flags)
            .Select(f => f.Name)
            .Where(n => n.Contains("reason", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("think", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("additional", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("raw", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        Console.WriteLine($"{label} interesting props: {string.Join(", ", props)}");
        Console.WriteLine($"{label} interesting fields: {string.Join(", ", fields)}");
    }

    private static void DumpAdditional(object? additional)
    {
        if (additional is null)
        {
            Console.WriteLine("Additional: <null>");
            return;
        }

        Console.WriteLine($"Additional type: {additional.GetType().FullName}");

        if (additional is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                Console.WriteLine($"  {entry.Key}: {FormatValue(entry.Value)}");
            }

            return;
        }

        if (additional is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                var k = entry is null ? null : GetPropertyValue(entry, "Key") as string;
                var v = entry is null ? null : GetPropertyValue(entry, "Value");
                if (k is not null)
                {
                    Console.WriteLine($"  {k}: {FormatValue(v)}");
                }
            }
        }
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value is JsonElement el)
        {
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
        }

        return value.ToString() ?? "";
    }
}