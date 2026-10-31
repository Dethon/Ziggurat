using Domain.Contracts;

namespace Domain.Prompts;

// The sandbox's doing rules, loaded when a request runs something. The section had no choosing
// rule of its own — that the sandbox is the one mount with exec is in the mounts list and in the
// stub — so it moved whole (docs/adr/0039). Built from the two values the mount declares about
// itself, as the prompt is, so a rename of the mount point cannot leave the prose quietly wrong.
// Its words and claims are Skills/sandbox.md; this is the handle the server and the scenarios hold.
public static class SandboxSkill
{
    public const string Name = "sandbox";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    private static readonly PromptTemplate _body =
        _document.Template("mount_point", "workspace", "native", "other_mounts");

    // The shell over the session's other mounts: where they are, what a command may do there, and
    // where it learns what actually happened. It has always run straight into the next heading.
    private static readonly string _otherMounts = _document.Part("other-mounts").Fill() + "\n";

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    // `servesMounts`, as for the prompt: the other-mounts section is taught only where a command
    // will find them.
    public static string Body(string mountPoint, string workspace, bool servesMounts = false) =>
        _body.Fill(
            ("mount_point", mountPoint),
            ("workspace", FileSystemResolution.ToVirtualPath(mountPoint, workspace)),
            // The same directory as the container spells it, which is what command output will show.
            ("native", "/" + workspace.TrimStart('/')),
            ("other_mounts", servesMounts ? _otherMounts : ""));

    public static SkillText For(string mountPoint, string workspace, bool servesMounts = false) =>
        new(Name, Description, Body(mountPoint, workspace, servesMounts));

    public static readonly PromptClaim LoadsForARun =
        _document.Claim("sandbox.loads-for-a-run");

    public static readonly PromptClaim WorksOnTheMountsInPlace =
        _document.Claim("sandbox.works-on-the-mounts-in-place");

    public static readonly PromptClaim ReportsWhatVfsChangesSays =
        _document.Claim("sandbox.reports-what-vfs-changes-says");

    public static readonly PromptClaim ActionsRunFromScripts =
        _document.Claim("sandbox.actions-run-from-scripts");
}