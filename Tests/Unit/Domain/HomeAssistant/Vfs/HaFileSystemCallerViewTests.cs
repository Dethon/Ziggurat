using System.Reflection;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The registrar asks the mount for itself as one caller sees it, once per call, and that view is
// the instance that answers — so whatever the server built the mount with, the view must have
// too. The failure this pins is silent: a dependency the view lost leaves the server's one
// instance working and the one that answers calls not, with nothing at compile time to say so.
// Asked of every instance field, so a dependency added tomorrow is covered without naming it.
public class HaFileSystemCallerViewTests
{
    private static readonly ConversationContext _jonas =
        new("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));

    private static HaFileSystem MountWithEveryDependency()
    {
        var client = new FakeHaClient();
        var time = new FakeTimeProvider();
        return new HaFileSystem(
            new HaCatalogProvider(() => client, time),
            () => client,
            regexMatchTimeout: TimeSpan.FromSeconds(3),
            musicClientFactory: () => Mock.Of<IMusicAssistantClient>(),
            timeProvider: time,
            caller: null,
            watches: new HaWatches(() => client, time),
            satellites: Mock.Of<ISatelliteCatalog>());
    }

    [Fact]
    public void AView_DiffersFromItsMount_InTheCallerAndNothingElse()
    {
        var mount = MountWithEveryDependency();

        var view = mount.For(_jonas).ShouldBeOfType<HaFileSystem>();

        var fields = typeof(HaFileSystem).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var differing = fields.Where(f => !Equals(f.GetValue(mount), f.GetValue(view))).ToList();

        var callerField = differing.ShouldHaveSingleItem();
        callerField.FieldType.ShouldBe(typeof(ConversationContext));
        callerField.GetValue(view).ShouldBe(_jonas);
        // A lost dependency would compare null to null and pass, so the mount above has to set every
        // one. The setup index is the exception: a cache built on first read, not a dependency.
        fields.Where(f => f != callerField && f.GetValue(mount) is null).Select(f => f.Name)
            .ShouldBe(["_setupIndex"]);
    }

    [Fact]
    public void AView_LeavesItsMountWithTheCallerItHad()
    {
        var mount = MountWithEveryDependency();

        var first = mount.For(_jonas);
        var second = mount.For(_jonas with { AgentId = "jack" });

        first.ShouldNotBeSameAs(mount);
        first.ShouldNotBeSameAs(second);
        CallerOf(mount).ShouldBeNull();
        CallerOf(first).ShouldBe(_jonas);
    }

    private static object? CallerOf(FileSystemBackendBase fs) =>
        typeof(HaFileSystem).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(ConversationContext)).GetValue(fs);
}