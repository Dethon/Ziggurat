using Domain.Contracts;
using Domain.Tools.Files;
using McpServerVault.Modules;
using McpServerVault.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpServerVault;

// The mount's own description is the last thing a model reads before it decides where to search,
// and it is per-deployment prose passed in at construction — so it is the place to say what the
// vault is not for. The vault prompt says it too, and glm-5.3-flash searched /vault for a recipe
// on a neighbourhood website anyway, three runs of three: "busca" was doing the work and the
// mount it reached for described itself only as a place full of notes.
public class VaultMountDescriptionTests
{
    private static string Description()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureMcp(new McpSettings
        {
            VaultPath = "/vault",
            AllowedExtensions = [".md"]
        });

        return services.BuildServiceProvider()
            .GetRequiredService<TextDiskFileSystem>()
            .DescribeMount;
    }

    [Fact]
    public void TheVaultMount_SaysItHoldsWhatTheUserWroteAndNotTheWorld()
    {
        var description = Description();

        description.ShouldContain("Obsidian");
        description.ShouldContain("the web", Case.Insensitive,
            "the mount a model picks for a search has to say which searches are not its own");
        // A booking on a site is not a question about the world, and the description that only
        // named questions left glm-5.3-flash searching the vault for the site's name before
        // filling its form.
        description.ShouldContain("a booking", Case.Insensitive,
            "a task on a site has to be named beside the questions, or the rule is read as " +
            "questions only");
    }
}