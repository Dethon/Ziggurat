using Domain.Tools.FileSystem;
using Domain.Tools.Memory;
using Domain.Tools.SubAgents;
using Domain.Tools.Web;

namespace Tests.Eval.Harness;

// The qualified names the model sees, composed from the tool constants rather than typed out.
// A scenario that spelled them as literals would keep passing after a rename, testing a tool
// nothing offers any more.
public static class EvalTools
{
    private const string Prefix = "domain__filesystem__";

    // Not a filesystem tool, and not judged like one: what a scenario declares about delegation is
    // which worker ran and what it was told, so the call itself answers to that declaration rather
    // than to the permitted set.
    public static readonly string Subagent = "domain__subagents__" + SubAgentRunTool.Name;

    // The only memory action there is: storing and recalling happen without the agent asking.
    public static readonly string Forget = "domain__memory__" + MemoryForgetTool.Name;

    // Served over MCP, so the name carries the endpoint the agent dialled — host and port — and
    // the port is whatever was free when the stack came up. The wildcard is matched as a pattern.
    private const string Served = "mcp__*__";

    public static readonly string WebSearch = Served + WebSearchTool.Name;
    public static readonly string WebBrowse = Served + WebBrowseTool.Name;
    public static readonly string WebSnapshot = Served + WebSnapshotTool.Name;
    public static readonly string WebAction = Served + WebActionTool.Name;

    public static readonly string Create = Prefix + VfsTextCreateTool.Name;
    public static readonly string Read = Prefix + VfsFileReadTool.Name;
    public static readonly string Glob = Prefix + VfsGlobFilesTool.Name;
    public static readonly string Info = Prefix + VfsFileInfoTool.Name;
    public static readonly string Remove = Prefix + VfsRemoveTool.Name;
    public static readonly string Exec = Prefix + VfsExecTool.Name;
    public static readonly string Search = Prefix + VfsTextSearchTool.Name;
    public static readonly string Edit = Prefix + VfsTextEditTool.Name;
    public static readonly string Move = Prefix + VfsMoveTool.Name;
    public static readonly string Copy = Prefix + VfsCopyTool.Name;
}