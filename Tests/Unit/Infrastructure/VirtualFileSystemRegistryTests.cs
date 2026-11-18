using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class VirtualFileSystemRegistryTests
{
    private readonly VirtualFileSystemRegistry _registry = new();

    [Fact]
    public void Mount_RegistersMount()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Personal document library"), backend);

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Name.ShouldBe("library");
        mounts[0].MountPoint.ShouldBe("/library");
    }

    // A mount point is a name the model addresses, so two mounts claiming one is a collision
    // somebody has to lose. The one already there wins, so which one loses is decided by mount
    // order rather than by timing. Outposts live at machine addresses of their own, so the only
    // claims that can collide there are two machines that gave themselves the same name.
    [Fact]
    public void Mount_AMountPointAlreadyTaken_IsShadowedAndTheExistingMountIsUntouched()
    {
        var laptop = CreateMockBackend("laptop");
        _registry.Mount(new FileSystemMount("laptop", "outpost:laptop", "The first laptop"), laptop);

        _registry.TryMount(
                new FileSystemMount("laptop", "outpost:laptop", "A second machine calling itself laptop"),
                CreateMockBackend("impostor"))
            .ShouldBeFalse();

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Description.ShouldBe("The first laptop");
        Resolve("outpost:laptop/notes.md").Backend.ShouldBe(laptop);
    }

    [Fact]
    public void Mount_AFreeMountPoint_IsTaken()
    {
        _registry.TryMount(new FileSystemMount("laptop", "outpost:laptop", "A laptop"), CreateMockBackend("laptop"))
            .ShouldBeTrue();

        _registry.GetMounts().Select(m => m.Name).ShouldBe(["laptop"]);
    }

    // A machine that named itself after a mount is a different thing at a different address, so
    // it no longer competes with the mount for the name — both are there, each where it is spelled.
    [Fact]
    public void Mount_AnOutpostNamedLikeAMount_IsMountedBesideIt()
    {
        var vault = CreateMockBackend("vault");
        var machine = CreateMockBackend("machine");
        _registry.TryMount(new FileSystemMount("vault", "/vault", "The real vault"), vault).ShouldBeTrue();

        _registry.TryMount(new FileSystemMount("vault", "outpost:vault", "A machine called vault"), machine)
            .ShouldBeTrue();

        Resolve("/vault/notes.md").Backend.ShouldBe(vault);
        Resolve("vault/notes.md").Backend.ShouldBe(vault);
        Resolve("outpost:vault/notes.md").Backend.ShouldBe(machine);
    }

    // The machine address is a whole spelling of its own, so it is resolved as given rather than
    // read as a mount name that forgot its slash.
    [Fact]
    public void Resolve_AMachineAddress_ResolvesAtTheMachineItNames()
    {
        var backend = CreateMockBackend("laptop");
        _registry.Mount(new FileSystemMount("laptop", "outpost:laptop", "A laptop"), backend);

        var resolution = Resolve("outpost:laptop/home/someone/notes.md");

        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("home/someone/notes.md");
        resolution.MountPoint.ShouldBe("outpost:laptop");
        Resolve("outpost:laptop").RelativePath.ShouldBe("");
        Resolve("OUTPOST:Laptop/x.md").Backend.ShouldBe(backend);
    }

    [Fact]
    public void Resolve_AMachineAddressNothingHasClaimed_IsAbsent()
    {
        _registry.Mount(new FileSystemMount("laptop", "outpost:laptop", "A laptop"), CreateMockBackend("laptop"));

        _registry.Resolve("outpost:laptopextra/x.md").TryGetValue(out _, out var error).ShouldBeFalse();
        error!.Message.ShouldContain("No filesystem mounted");
        _registry.Resolve("/laptop/x.md").TryGetValue(out _, out _).ShouldBeFalse();
    }

    // "ha/setup-index.md" names a mount and forgot the slash. It cannot mean anything else, and
    // refusing it with "paths start at a mount point" bought one more call that said the same
    // thing with the slash on.
    [Fact]
    public void Resolve_APathMissingItsLeadingSlash_ResolvesAtTheMountItNames()
    {
        var backend = CreateMockBackend("ha");
        _registry.Mount(new FileSystemMount("ha", "/ha", "The home"), backend);

        var resolution = Resolve("ha/setup-index.md");

        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("setup-index.md");
        resolution.MountPoint.ShouldBe("/ha");
    }

    [Fact]
    public void Resolve_MatchingMount_ReturnsBackendAndRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/library/notes/todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("notes/todo.md");
    }

    [Fact]
    public void Resolve_RootPath_ReturnsEmptyRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/library");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("");
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        var libraryBackend = CreateMockBackend("library");
        var docsBackend = CreateMockBackend("docs");

        _registry.Mount(new FileSystemMount("library", "/library", "Library"), libraryBackend);
        _registry.Mount(new FileSystemMount("docs", "/library/docs", "Docs"), docsBackend);

        var resolution = Resolve("/library/docs/readme.md");
        resolution.Backend.ShouldBe(docsBackend);
        resolution.RelativePath.ShouldBe("readme.md");
    }

    // Resolution answers with data, never an exception: the tool sites hand the envelope straight
    // to the model, which is what makes the "errors are data" promise in the prompt true.
    [Fact]
    public void Resolve_NoMatchingMount_ReturnsAnErrorNamingThePathAndTheMounts()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        _registry.Resolve("/unknown/file.md").TryGetValue(out _, out var error).ShouldBeFalse();

        // A name nothing here has ever had: absent, so nothing to wait for, and the recovery is
        // the list of mounts that do exist.
        error!.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        error.Retryable.ShouldBeFalse();
        error.Message.ShouldContain("No filesystem mounted");
        error.Message.ShouldContain("/unknown/file.md");
        error.Recovery!.ShouldContain("/library");
    }

    // The same miss, for a machine that registered and then did not answer when this conversation
    // started. It is the one absence worth trying again, and reading it as a typo — which is what
    // every miss used to say — sends the model looking for a path that was spelled right.
    [Fact]
    public void Resolve_AMountThisSessionKnowsIsUnreachable_SaysSoAndInvitesARetry()
    {
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), CreateMockBackend("library"));
        _registry.DeclareAbsence("outpost:laptop", CapabilityState.Unavailable, "it did not answer when this conversation started");

        _registry.Resolve("outpost:laptop/notes.md").TryGetValue(out _, out var error).ShouldBeFalse();

        error!.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Retryable.ShouldBeTrue();
        error.Message.ShouldContain("did not answer");
        error.Message.ShouldContain("'outpost:laptop'");
    }

    // The plain Mount obeys the same rule as TryMount above — this used to be last-write-wins, and
    // a second claim on a name must never quietly replace what the model was already using.
    [Fact]
    public void Mount_DuplicateMountPoint_LeavesTheFirstOneInPlace()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        _registry.Mount(new FileSystemMount("lib1", "/library", "First"), backend1);
        _registry.Mount(new FileSystemMount("lib2", "/library", "Second"), backend2);

        Resolve("/library/file.md").Backend.ShouldBe(backend1);
    }

    [Fact]
    public void Resolve_CaseInsensitiveMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/Library/Notes/Todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("Notes/Todo.md");
    }

    [Fact]
    public void Resolve_PartialSegmentMatch_DoesNotMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        _registry.Resolve("/libraryextra/file.md").TryGetValue(out _, out var error).ShouldBeFalse();

        error!.Message.ShouldContain("No filesystem mounted");
    }

    private static FileSystemMount Watches(string name, FileSystemUnion? union, string description = "") =>
        new(name, "/watchers", description == "" ? $"The {name}." : description)
        {
            Capabilities = name == "ha-watches" ? ["file_read", "text_create", "glob", "remove"] : ["file_read", "glob", "text_create"],
            Union = union
        };

    private static readonly FileSystemUnion _homeUnion = new("/triggers/*/trigger", [], Fallback: true);
    private static readonly FileSystemUnion _sugaryUnion = new("/triggers/*/trigger", ["glucose", "sensor"]);

    // Filesystems that all declare a union share their mount point: one mount, both members behind
    // it, its description theirs, its operations every member's.
    [Fact]
    public void TryMount_UnionMembersAtOneMountPoint_AreOneMount()
    {
        _registry.TryMount(Watches("ha-watches", _homeUnion, "Home watches."), CreateMockBackend("ha-watches")).ShouldBeTrue();
        _registry.TryMount(Watches("sugary-watches", _sugaryUnion, "Glucose watches."), CreateMockBackend("sugary-watches")).ShouldBeTrue();

        var mount = _registry.GetMounts().ShouldHaveSingleItem();
        mount.MountPoint.ShouldBe("/watchers");
        mount.Name.ShouldBe("ha-watches+sugary-watches");
        mount.Description.ShouldBe("Home watches. Glucose watches.");
        mount.Capabilities.ShouldBe(["file_read", "text_create", "glob", "remove"]);
        mount.ShellReach.ShouldBeNull();
        mount.Workspace.ShouldBeNull();
        mount.IsLandingTarget.ShouldBeFalse();
        Resolve("/watchers/low/watch.json").Backend.ShouldBeOfType<UnionFileSystem>()
            .Members.Select(m => m.Name).ShouldBe(["ha-watches", "sugary-watches"]);
    }

    [Fact]
    public void TryMount_AThirdMember_JoinsTheUnion()
    {
        _registry.TryMount(Watches("ha-watches", _homeUnion), CreateMockBackend("ha-watches"));
        _registry.TryMount(Watches("sugary-watches", _sugaryUnion), CreateMockBackend("sugary-watches"));

        _registry.TryMount(Watches("weather", new FileSystemUnion("/triggers/*/trigger", ["rain"])), CreateMockBackend("weather"))
            .ShouldBeTrue();

        Resolve("/watchers").Backend.ShouldBeOfType<UnionFileSystem>()
            .Members.Select(m => m.Name).ShouldBe(["ha-watches", "sugary-watches", "weather"]);
    }

    // A mount that did not ask to share keeps first-wins, whichever side declared the union.
    [Fact]
    public void TryMount_AUnionMemberMeetingAPlainMount_IsShadowed()
    {
        var plain = CreateMockBackend("plain");
        _registry.TryMount(Watches("plain", null), plain).ShouldBeTrue();

        _registry.TryMount(Watches("sugary-watches", _sugaryUnion), CreateMockBackend("sugary-watches"), out var refusal)
            .ShouldBeFalse();

        refusal.ShouldNotBeNull();
        Resolve("/watchers/x").Backend.ShouldBe(plain);
    }

    [Fact]
    public void TryMount_APlainMountMeetingAUnion_IsShadowed()
    {
        _registry.TryMount(Watches("ha-watches", _homeUnion), CreateMockBackend("ha-watches"));

        _registry.TryMount(Watches("plain", null), CreateMockBackend("plain")).ShouldBeFalse();

        _registry.GetMounts().ShouldHaveSingleItem().Name.ShouldBe("ha-watches");
    }

    public static TheoryData<string, FileSystemMount> MembersThatCannotShare() => new()
    {
        { "workspace", Watches("box", _sugaryUnion) with { Workspace = "home" } },
        { "shell reach", Watches("box", _sugaryUnion) with { ShellReach = ShellReach.Contained } },
        { "landing target", Watches("box", _sugaryUnion) with { IsLandingTarget = true } }
    };

    // A member is a store of entries: where a command runs or a person's files land is no merge.
    // Refused whether it arrives first or second, so the outcome does not hang on dial order.
    [Theory]
    [MemberData(nameof(MembersThatCannotShare))]
    public void TryMount_AUnionMemberWithAWorkspaceShellOrLanding_IsRefusedAsAMember(string claim, FileSystemMount member)
    {
        _registry.TryMount(member, CreateMockBackend("box"), out var first).ShouldBeFalse(claim);
        first.ShouldNotBeNull().ShouldContain("box");

        _registry.TryMount(Watches("ha-watches", _homeUnion), CreateMockBackend("ha-watches")).ShouldBeTrue();
        _registry.TryMount(member, CreateMockBackend("box"), out var second).ShouldBeFalse(claim);
        second.ShouldNotBeNull();
        _registry.GetMounts().ShouldHaveSingleItem().Name.ShouldBe("ha-watches");
    }

    [Fact]
    public void TryMount_ASecondFallback_IsRefused()
    {
        _registry.TryMount(Watches("ha-watches", _homeUnion), CreateMockBackend("ha-watches"));

        _registry.TryMount(Watches("other", _homeUnion), CreateMockBackend("other"), out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain("fallback");
    }

    [Fact]
    public void TryMount_AValueAlreadyClaimed_IsRefused()
    {
        _registry.TryMount(Watches("sugary-watches", _sugaryUnion), CreateMockBackend("sugary-watches"));

        _registry.TryMount(Watches("other", new FileSystemUnion("/triggers/*/trigger", ["sensor"])), CreateMockBackend("other"), out var refusal)
            .ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain("'sensor'");
    }

    [Fact]
    public void TryMount_AMemberRoutingByAnotherPointer_IsRefused()
    {
        _registry.TryMount(Watches("ha-watches", _homeUnion), CreateMockBackend("ha-watches"));

        _registry.TryMount(Watches("other", new FileSystemUnion("/kind", ["x"])), CreateMockBackend("other"), out var refusal)
            .ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain("/kind");
    }

    private FileSystemResolution Resolve(string virtualPath)
    {
        _registry.Resolve(virtualPath).TryGetValue(out var resolution, out var error)
            .ShouldBeTrue(error?.Message);
        return resolution!;
    }

    private static IFileSystemBackend CreateMockBackend(string name)
    {
        var mock = new Mock<IFileSystemBackend>();
        mock.Setup(b => b.FilesystemName).Returns(name);
        return mock.Object;
    }
}