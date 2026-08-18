using Domain.Tools.FileSystem;

namespace Tests.Eval.Harness;

// The qualified names the model sees, composed from the tool constants rather than typed out.
// A scenario that spelled them as literals would keep passing after a rename, testing a tool
// nothing offers any more.
public static class EvalTools
{
    private const string Prefix = "domain__filesystem__";

    public static readonly string Create = Prefix + VfsTextCreateTool.Name;
    public static readonly string Read = Prefix + VfsFileReadTool.Name;
    public static readonly string Glob = Prefix + VfsGlobFilesTool.Name;
    public static readonly string Info = Prefix + VfsFileInfoTool.Name;
    public static readonly string Remove = Prefix + VfsRemoveTool.Name;
    public static readonly string Exec = Prefix + VfsExecTool.Name;
    public static readonly string Search = Prefix + VfsTextSearchTool.Name;
    public static readonly string Edit = Prefix + VfsTextEditTool.Name;
    public static readonly string Move = Prefix + VfsMoveTool.Name;
}