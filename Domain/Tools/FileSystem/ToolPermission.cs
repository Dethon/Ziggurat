namespace Domain.Tools.FileSystem;

// Whether a tool call would run with nobody asked, as the approval client would decide it right
// now: always approved, whitelisted, or remembered for this conversation. The approval client puts
// it on each call's own arguments, so a tool that acts on the conversation's behalf without a model
// call of its own — the exec bridge, answering a sandbox command's file operations — asks the same
// question the person's approval prompt answers, without guessing and without waiting on anyone.
public sealed class ToolPermission(Func<string, bool> runsUnasked)
{
    public static readonly object ContextKey = typeof(ToolPermission);

    // Nothing runs unasked: what a call with no approval client behind it gets.
    public static readonly ToolPermission None = new(_ => false);

    public bool RunsUnasked(string toolName) => runsUnasked(toolName);
}