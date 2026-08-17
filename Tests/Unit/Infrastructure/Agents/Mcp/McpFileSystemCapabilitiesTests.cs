using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.Mcp;

public class McpFileSystemCapabilitiesTests
{
    // The same two steps discovery composes: raw tool names resolve to operations, operations
    // derive the published capability list.
    private static IReadOnlyList<string> Derive(params string[] advertisedToolNames) =>
        McpFileSystemDiscovery.DeriveCapabilities(
            McpFileSystemDiscovery.AdvertisedOperations(advertisedToolNames));

    [Fact]
    public void DeriveCapabilities_MapsAdvertisedFsToolsToDomainLeafNames_InCanonicalOrder()
    {
        // Home Assistant advertises only read/info/glob/search/exec.
        var caps = Derive("fs_glob", "fs_info", "fs_read", "fs_search", "fs_exec");

        caps.ShouldBe(["file_read", "glob", "text_search", "file_info", "exec"]);
    }

    [Fact]
    public void DeriveCapabilities_OmitsOperationsTheServerDoesNotExpose()
    {
        // Printer omits fs_move and fs_exec; it does expose create/edit/copy/delete.
        var caps = Derive(
            "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_delete", "fs_copy",
            "fs_blob_read", "fs_blob_write");

        caps.ShouldNotContain("move");
        caps.ShouldNotContain("exec");
        caps.ShouldContain("text_create");
        caps.ShouldContain("copy");
        caps.ShouldContain("remove");
    }

    [Fact]
    public void DeriveCapabilities_IgnoresBlobAndNonFilesystemTools()
    {
        var caps = Derive("fs_read", "fs_blob_read", "fs_blob_write", "send_reply", "some_other_tool");

        caps.ShouldBe(["file_read"]);
    }

    [Fact]
    public void DeriveCapabilities_MatchesPrefixedToolNames()
    {
        // Aggregated agent-side names are namespaced (mcp__server__fs_glob); derivation must still match.
        var caps = Derive("mcp__mcp-homeassistant__fs_glob", "mcp__mcp-homeassistant__fs_exec");

        caps.ShouldBe(["glob", "exec"]);
    }
}