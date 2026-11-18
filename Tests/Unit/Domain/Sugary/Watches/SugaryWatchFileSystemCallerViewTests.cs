using System.Reflection;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Watches;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary.Watches;

// The registrar asks the mount for itself as one caller sees it, once per call: the view must carry
// every dependency the server built the mount with, and differ only in who is calling.
public class SugaryWatchFileSystemCallerViewTests
{
    private static readonly Account _her = new("household-home", "user-her");

    private static readonly ConversationContext _turn = new("jonas", "conv-1", _her.UserId,
        new ReplyTarget("signalr", "conv-1"), Principal: new Principal("signalr", _her.UserId, _her));

    [Fact]
    public void AView_DiffersFromItsMount_InTheCallerAndNothingElse()
    {
        var mount = new SugaryWatchFileSystem(
            Mock.Of<IGlucoseWatches>(), Mock.Of<IHouseholdVoices>(), TimeZoneInfo.Utc, new FakeTimeProvider());
        var household = Mock.Of<ICallHousehold>();

        var view = mount.For(new FileSystemCaller(_turn, null, Household: household)).ShouldBeOfType<SugaryWatchFileSystem>();

        var fields = typeof(SugaryWatchFileSystem).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var differing = fields.Where(f => !Equals(f.GetValue(mount), f.GetValue(view))).ToList();

        differing.Select(f => f.GetValue(view)).ShouldBe([_turn, _her, household], ignoreOrder: true);
        fields.Where(f => !differing.Contains(f) && f.GetValue(mount) is null).Select(f => f.Name).ShouldBeEmpty();
    }
}