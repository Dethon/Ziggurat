using System.Text.Json;
using Domain.Prompts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Infrastructure.Utils;

// The twin of AddFileSystemResource, for skills. A tool server ships the skills that teach its
// tools as resources in the standard shape — one index document naming every skill and where its
// body is, and one body per skill as markdown under a name-and-description frontmatter — and both
// are built here from the constants beside the skill's declaration, so no server hand-writes a
// resource and the wire shape is the same for every server (docs/adr/0039).
//
// The reader is McpClientManager, which finds the index by its address, reads each body it names
// and binds it to the manifest by the frontmatter's name.
public static class SkillServerResources
{
    private const string Scheme = "skills://";

    public const string IndexAddress = Scheme + "index";

    public const string IndexMimeType = "application/json";

    public const string BodyMimeType = "text/markdown";

    public static IMcpServerBuilder AddSkills(this IMcpServerBuilder builder, params SkillText[] skills)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(skills);

        var index = Index(skills);
        builder.Services.AddSingleton(McpServerResource.Create(
            () => index,
            new McpServerResourceCreateOptions
            {
                UriTemplate = IndexAddress,
                Name = "skills",
                Description = "The skills this server ships: name, description and where each body is.",
                MimeType = IndexMimeType
            }));

        foreach (var skill in skills)
        {
            var body = Body(skill);
            builder.Services.AddSingleton(McpServerResource.Create(
                () => body,
                new McpServerResourceCreateOptions
                {
                    UriTemplate = BodyAddress(skill.Name),
                    Name = skill.Name,
                    Description = skill.Description,
                    MimeType = BodyMimeType
                }));
        }

        return builder;
    }

    // A skill whose body is built from what the server publishes — the sandbox's mount point and
    // workspace — rather than from constants alone. The name and description are constants, so
    // the index is; the body waits for the provider.
    public static IMcpServerBuilder AddSkill(
        this IMcpServerBuilder builder, string name, string description, Func<IServiceProvider, string> body)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(body);

        var index = Index([new SkillText(name, description, string.Empty)]);
        builder.Services.AddSingleton(McpServerResource.Create(
            () => index,
            new McpServerResourceCreateOptions
            {
                UriTemplate = IndexAddress,
                Name = "skills",
                Description = "The skills this server ships: name, description and where each body is.",
                MimeType = IndexMimeType
            }));
        builder.Services.AddSingleton(sp => McpServerResource.Create(
            () => Body(new SkillText(name, description, body(sp))),
            new McpServerResourceCreateOptions
            {
                UriTemplate = BodyAddress(name),
                Name = name,
                Description = description,
                MimeType = BodyMimeType
            }));

        return builder;
    }

    public static string BodyAddress(string skillName) => $"{Scheme}{skillName}/SKILL.md";

    // What the index says about each skill: enough to advertise it and to fetch it, nothing of the
    // body. `type` names the shape of what the location holds, so a later kind of skill can sit in
    // the same index without the reader guessing.
    public static string Index(IEnumerable<SkillText> skills) =>
        JsonSerializer.Serialize(new
        {
            skills = skills.Select(s => new
            {
                name = s.Name,
                type = "skill",
                description = s.Description,
                location = BodyAddress(s.Name)
            })
        });

    // The body as the model would read it from a skills directory: frontmatter, then the markdown.
    public static string Body(SkillText skill) =>
        $"---\nname: {skill.Name}\ndescription: {skill.Description}\n---\n\n{skill.Body.TrimEnd()}\n";
}