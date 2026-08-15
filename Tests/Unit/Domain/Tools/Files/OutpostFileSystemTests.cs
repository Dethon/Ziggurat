using Domain.Contracts;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

// An outpost is a text disk root over a real machine, mounted under the name its operator gave it,
// with the machine's own root as the mount root. What is worth pinning here is the prose, because
// it is generated from the values the binary was started with rather than written — which is what
// stops it disagreeing with the behaviour.
public class OutpostFileSystemTests
{
    [Fact]
    public void TheMountPoint_IsTheNameItWasGiven()
    {
        Outpost("laptop", "/home/someone/project").MountPoint.ShouldBe("/laptop");
    }

    // The mount root is the machine's root whether the outpost is jailed or not, so the same file
    // has the same name either way. Jailing changes what an operation will do, never what a path is
    // called.
    [Fact]
    public void TheWorkspace_IsTheWorkingDirectoryInTheMountsOwnCoordinates()
    {
        Outpost("laptop", "/home/someone/project").Workspace.ShouldBe("home/someone/project");
    }

    // A trailing slash is how a person spells a directory and must not become part of the name.
    [Fact]
    public void ATrailingSlashOnTheWorkingDirectory_IsNotPartOfIt()
    {
        Outpost("laptop", "/home/someone/project/").Workspace.ShouldBe("home/someone/project");
    }

    [Fact]
    public void TheGeneratedDescription_NamesTheMachineAndItsWorkingDirectory()
    {
        var description = Outpost("laptop", "/home/someone/project").DescribeMount;

        description.ShouldContain("laptop");
        description.ShouldContain("/home/someone/project");
    }

    // Every fact in the prose is read off the outpost itself, so an outpost that cannot execute
    // says so rather than leaving the model to find out by being refused.
    [Fact]
    public void APlainOutpost_SaysItIsNeitherJailedNorAbleToRunCommands()
    {
        var description = Outpost("laptop", "/home/someone/project").DescribeMount;

        description.ShouldContain("Not jailed");
        description.ShouldContain("Commands cannot be run");
    }

    // A working directory is a path on somebody's machine, so a relative one has nothing to be
    // relative to when the binary is started from anywhere. A configuration mistake, refused before
    // the server can serve anything.
    [Theory]
    [InlineData("project")]
    [InlineData("./project")]
    [InlineData("")]
    [InlineData("   ")]
    public void AWorkingDirectoryThatIsNotAnAbsolutePath_FailsAtConstruction(string dir)
    {
        Should.Throw<ArgumentException>(() => Outpost("laptop", dir));
    }

    // The machine root means the whole machine, which is what somebody serving their computer
    // wants. It is kept as "/" rather than trimmed away, so the jail and the prose have a path to
    // name.
    [Fact]
    public void TheMachineRootAsAWorkingDirectory_IsTheWholeMachine()
    {
        var outpost = Outpost("laptop", "/");

        outpost.Workspace.ShouldBe("");
        outpost.DescribeMount.ShouldContain("/laptop/");
    }

    private static OutpostFileSystem Outpost(string name, string workingDirectory) =>
        new(name, Mock.Of<IFileSystemClient>(), workingDirectory, [".md"]);
}