using Infrastructure.Agents;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class ThreadSessionTests(ThreadSessionServerFixture fixture)
    : IClassFixture<ThreadSessionServerFixture>
{
    [Fact]
    public async Task CreateSession_InitializesWithToolsAndPrompts()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var session = await ThreadSession.CreateAsync(
            [McpServerEndpoint.Configured(fixture.McpEndpoint, McpTestSecret.Value)],
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
        session.ClientManager.Prompts.Any(p => p.Text.Contains("test assistant", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue("Should contain the test system prompt");

        await session.DisposeAsync();
    }
}