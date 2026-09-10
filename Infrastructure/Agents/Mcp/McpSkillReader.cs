using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Prompts;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

// Reads the skills a server ships, in the shape SkillServerResources publishes them: the index at
// its known address, then one body per entry. A server with no index ships no skills, and a body
// the index names but the server cannot serve is logged and skipped rather than failing the
// session — a skill that is missing is a call the model will not make, which is the deployment's
// problem and not this turn's.
//
// A listing or index that *failed*, though, is not an answer, and this throws it. The caller
// swallows it per session, so a session still builds; what must not happen is a cache storing the
// empty list a swallow here would return — a server that was restarting for one fetch would then
// have no skills for the whole ttl, and a refresh landing in that moment would replace a good
// list with nothing, while the stub telling the model to load them is cached separately and goes
// on saying so. A throw is what McpPromptCache's own failure handling is written against.
internal static partial class McpSkillReader
{
    public static async Task<PromptSkill[]> ReadAsync(McpClient client, ILogger? logger, CancellationToken ct)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        if (!resources.Any(r => string.Equals(r.Uri, SkillServerResources.IndexAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var index = JsonSerializer.Deserialize<SkillIndex>(
            await TextAsync(client, SkillServerResources.IndexAddress, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var skills = await Task.WhenAll((index?.Skills ?? []).Select(async entry =>
        {
            try
            {
                var served = Parse(await TextAsync(client, entry.Location, ct));
                var name = served.Name ?? entry.Name;
                var description = served.Description ?? entry.Description ?? string.Empty;
                var skill = PromptManifest.BindSkill(name, description, served.Body);
                if (!skill.Declaration.Declared)
                {
                    logger?.LogWarning(
                        "Skill '{Skill}' is served but not declared in the manifest; it is offered under the "
                        + "default budgets with no stated server",
                        name);
                }

                return skill;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "The skill '{Skill}' at {Location} could not be read, so it is not offered",
                    entry.Name, entry.Location);
                return null;
            }
        }));

        return [.. skills.OfType<PromptSkill>()];
    }

    private static async Task<string> TextAsync(McpClient client, string uri, CancellationToken ct)
    {
        var content = await client.ReadResourceAsync(uri, cancellationToken: ct);
        return string.Join("", content.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    // The frontmatter is the skill's own statement of its name and description; the body is what
    // follows it. A body with no frontmatter is taken whole, named by the index.
    internal static (string? Name, string? Description, string Body) Parse(string text)
    {
        var match = Frontmatter().Match(text);
        if (!match.Success)
        {
            return (null, null, text.Trim());
        }

        var fields = match.Groups[1].Value.Split('\n')
            .Select(line => (Separator: line.IndexOf(':'), Line: line))
            .Where(field => field.Separator > 0)
            .ToDictionary(
                field => field.Line[..field.Separator].Trim(),
                field => field.Line[(field.Separator + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);

        return (
            fields.GetValueOrDefault("name"),
            fields.GetValueOrDefault("description"),
            text[(match.Index + match.Length)..].Trim());
    }

    [GeneratedRegex(@"\A﻿?---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    private sealed record SkillIndex(IReadOnlyList<SkillIndexEntry> Skills);

    private sealed record SkillIndexEntry(string Name, string? Type, string? Description, string Location);
}