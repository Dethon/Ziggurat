using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.Mcp;

// What discovery makes of a filesystem resource body. Each claim a mount publishes arrives on the
// mount the registry holds, and a server that predates a claim publishes no field, which must read
// as the claim not made.
public class McpFileSystemMountReadTests
{
    private static readonly IReadOnlySet<string> Exec = new HashSet<string> { "fs_exec" };
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Theory]
    [InlineData("contained", ShellReach.Contained)]
    [InlineData("host", ShellReach.Host)]
    public void APublishedShellReach_IsCarriedOnTheMount(string published, ShellReach expected)
    {
        var mount = McpFileSystemDiscovery.ReadMount(
            $$"""{"name":"box","mountPoint":"/box","description":"d","shellReach":"{{published}}"}""", Exec);

        mount.ShouldNotBeNull().Mount.ShellReach.ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"name":"box","mountPoint":"/box","description":"d"}""")]
    [InlineData("""{"name":"box","mountPoint":"/box","description":"d","shellReach":null}""")]
    public void NoShellReach_ReadsAsNone(string json)
    {
        McpFileSystemDiscovery.ReadMount(json, Exec).ShouldNotBeNull().Mount.ShellReach.ShouldBeNull();
    }

    // A reach this agent does not know is not a reach it can reason about. Read as none rather than
    // refusing the mount: the mount still works, and the screen skips what it cannot place.
    [Fact]
    public void AnUnknownShellReach_ReadsAsNone()
    {
        McpFileSystemDiscovery.ReadMount(
                """{"name":"box","mountPoint":"/box","description":"d","shellReach":"orbit"}""", None)
            .ShouldNotBeNull().Mount.ShellReach.ShouldBeNull();
    }

    [Fact]
    public void TheOtherClaims_AreCarriedBesideIt()
    {
        var mount = McpFileSystemDiscovery.ReadMount(
            """{"name":"box","mountPoint":"/box","description":"d","workspace":"home/u","landingTarget":true}""",
            Exec)?.Mount;

        mount.ShouldNotBeNull();
        mount.Workspace.ShouldBe("home/u");
        mount.IsLandingTarget.ShouldBeTrue();
        mount.Capabilities.ShouldBe(["exec"]);
    }

    [Fact]
    public void ABodyWithNoName_IsNoMount()
    {
        McpFileSystemDiscovery.ReadMount("""{"mountPoint":"/box"}""", None).ShouldBeNull();
    }

    // A server may serve several filesystems, so its tool list is the union of theirs; each
    // resource says which operations are its own, and the mount gets those alone.
    [Fact]
    public void PublishedOperations_AreTheMountsOwn()
    {
        var discovered = McpFileSystemDiscovery.ReadMount(
            """{"name":"box","mountPoint":"/box","description":"d","operations":["fs_read","fs_blob_read"]}""",
            new HashSet<string> { "fs_read", "fs_exec", "fs_blob_read" });

        discovered.ShouldNotBeNull().Mount.Capabilities.ShouldBe(["file_read"]);
        discovered.Operations.ShouldBe(["fs_read", "fs_blob_read"], ignoreOrder: true);
    }

    // A published operation the server has no tool for cannot be called, so it is not offered.
    [Fact]
    public void APublishedOperationTheServerDoesNotAdvertise_IsNotTheMounts()
    {
        McpFileSystemDiscovery.ReadMount(
                """{"name":"box","mountPoint":"/box","description":"d","operations":["fs_read","fs_exec"]}""",
                new HashSet<string> { "fs_read" })
            .ShouldNotBeNull().Operations.ShouldBe(["fs_read"]);
    }

    // An outpost or a kit server from before publishes no operations: its mount keeps the server's
    // whole tool list, as every mount did when a server served one filesystem.
    [Fact]
    public void NoPublishedOperations_FallBackToTheServers()
    {
        var discovered = McpFileSystemDiscovery.ReadMount(
            """{"name":"box","mountPoint":"/box","description":"d"}""",
            new HashSet<string> { "fs_read", "fs_exec" });

        discovered.ShouldNotBeNull().Mount.Capabilities.ShouldBe(["file_read", "exec"]);
        discovered.Operations.ShouldBe(["fs_read", "fs_exec"], ignoreOrder: true);
    }

    // Empty is a claim — this mount serves nothing — not the absence of one.
    [Fact]
    public void AnEmptyOperationsList_IsNoOperations()
    {
        McpFileSystemDiscovery.ReadMount(
                """{"name":"box","mountPoint":"/box","description":"d","operations":[]}""",
                new HashSet<string> { "fs_read" })
            .ShouldNotBeNull().Operations.ShouldBeEmpty();
    }

    // The union a backend declares goes out spelled as the other claims are (camelCase) and comes
    // back on the mount the registry holds.
    [Fact]
    public void ADeclaredUnion_IsPublishedAndCarriedOnTheMount()
    {
        var body = FileSystemServerResource.Describe(new SharingMount());

        body.ShouldContain("""
            "union":{"routeBy":"/triggers/*/trigger","claims":["glucose","sensor"],"fallback":false}
            """);
        var union = McpFileSystemDiscovery.ReadMount(body, None).ShouldNotBeNull().Mount.Union.ShouldNotBeNull();
        union.RouteBy.ShouldBe("/triggers/*/trigger");
        union.Claims.ShouldBe(["glucose", "sensor"]);
        union.Fallback.ShouldBeFalse();
    }

    [Fact]
    public void AFallbackUnion_ReadsWithNoClaims()
    {
        McpFileSystemDiscovery.ReadMount(
                """{"name":"box","mountPoint":"/w","description":"d","union":{"routeBy":"/k","fallback":true}}""", None)
            .ShouldNotBeNull().Mount.Union.ShouldBe(new FileSystemUnion("/k", [], true), new UnionComparer());
    }

    [Theory]
    [InlineData("""{"name":"box","mountPoint":"/w","description":"d"}""")]
    [InlineData("""{"name":"box","mountPoint":"/w","description":"d","union":null}""")]
    [InlineData("""{"name":"box","mountPoint":"/w","description":"d","union":{"claims":["x"]}}""")]
    public void NoUnionOrOneRoutingByNothing_ReadsAsNone(string json)
    {
        McpFileSystemDiscovery.ReadMount(json, None).ShouldNotBeNull().Mount.Union.ShouldBeNull();
    }

    [Fact]
    public void AMountDeclaringNoUnion_PublishesNull()
    {
        FileSystemServerResource.Describe(new PlainMount()).ShouldContain("\"union\":null");
    }

    private sealed class UnionComparer : IEqualityComparer<FileSystemUnion?>
    {
        public bool Equals(FileSystemUnion? x, FileSystemUnion? y) =>
            x is not null && y is not null && x.RouteBy == y.RouteBy && x.Fallback == y.Fallback && x.Claims.SequenceEqual(y.Claims);

        public int GetHashCode(FileSystemUnion? obj) => 0;
    }

    private sealed class SharingMount : FileSystemBackendBase
    {
        public override string FilesystemName => "sugary-watches";
        public override string MountPoint => "/watchers";
        public override string DescribeMount => "Glucose watches.";
        public override FileSystemUnion Union => new("/triggers/*/trigger", ["glucose", "sensor"]);
    }

    private sealed class PlainMount : FileSystemBackendBase
    {
        public override string FilesystemName => "plain";
        public override string DescribeMount => "Plain.";
    }
}