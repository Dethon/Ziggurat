using Domain.Contracts;

namespace Domain.Prompts;

// The sandbox's standing prose (Sections/sandbox_prompt.md), served by mcp-sandbox, built from the
// mount point the mount declares rather than asserting it, so a rename cannot leave it quietly
// wrong.
public static class SandboxPrompt
{
    public const string Name = "sandbox_prompt";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    private static readonly PromptTemplate _prompt = _document.Template("mount_point", "workspace", "serves_mounts");

    private static readonly string _servesMounts = _document.Part("serves-mounts").Fill();

    // `workspace` arrives in the backend's own coordinates, as the mount publishes it, and is
    // composed into a virtual path here by the one translation — the model never reads a backend
    // spelling (ADR 0016).
    //
    // `servesMounts`: this sandbox serves the session's other mounts to its commands (it has a
    // launcher and an exec bridge to ask), so the prose can say so; otherwise it says nothing about
    // them.
    public static string Build(string mountPoint, string workspace, bool servesMounts = false) =>
        _prompt.Fill(
            ("mount_point", mountPoint),
            ("workspace", FileSystemResolution.ToVirtualPath(mountPoint, workspace)),
            ("serves_mounts", servesMounts ? " " + _servesMounts : ""));
}