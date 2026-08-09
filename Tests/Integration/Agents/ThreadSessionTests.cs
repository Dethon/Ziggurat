using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class ThreadSessionTests(ThreadSessionServerFixture fixture)
    : IClassFixture<ThreadSessionServerFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<ThreadSessionTests>()
        .Build();

    private static OpenRouterChatClient CreateChatClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterChatClient(apiUrl, apiKey, "~deepseek/deepseek-v4-flash-latest");
    }

    [SkippableFact]
    public async Task CreateSession_InitializesWithToolsAndPrompts()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            "TestClient",
            "test-user",
            "Test Description",
            [],
            new HashSet<string>(),
            null,
            cts.Token);

        // Assert - session and managers initialized
        session.ShouldNotBeNull();
        session.ClientManager.ShouldNotBeNull();
        session.ClientManager.Clients.ShouldNotBeEmpty();

        // Assert - tools loaded from server
        session.ClientManager.Tools.ShouldNotBeEmpty();
        var toolNames = session.ClientManager.Tools.Select(t => t.Name).ToList();
        toolNames.ShouldContain(n => n.EndsWith("__Echo"));

        // Assert - prompts loaded from server
        session.ClientManager.Prompts.ShouldNotBeEmpty();
        session.ClientManager.Prompts.Any(p => p.Contains("test assistant", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue("Should contain the test system prompt");

        await session.DisposeAsync();
    }
}