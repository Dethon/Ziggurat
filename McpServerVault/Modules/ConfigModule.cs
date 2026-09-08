using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerVault.McpPrompts;
using McpServerVault.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerVault.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                "vault",
                // The reusable disk root takes the mount's prose the same way it takes its name:
                // "Obsidian vault" is this deployment's, not every text root's.
                $"Personal Obsidian vault ({settings.VaultPath}) — markdown notes with wikilinks, "
                + "embeds, frontmatter, and tags; the user edits the same files in Obsidian. "
                + "Persistent host-mounted directory. Read/write text only (allowed extensions "
                + "enforced); does NOT support fs_exec. See the Vault Filesystem (Obsidian) prompt "
                + "for conventions.",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.VaultPath),
                settings.AllowedExtensions))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<TextDiskFileSystem>()
            .AddFileSystemResource<TextDiskFileSystem>()
            // The skill that teaches this server's tools ships beside them, so a deployment
            // without the vault cannot advertise how to write a note.
            .AddSkills(ObsidianVaultSkill.Text)
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}