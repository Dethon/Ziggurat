using Dashboard.Client.State.Connection;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class ConnectionStoreTests
{
    [Fact]
    public void InitialState_BeforeAnythingConnects_IsConnecting()
    {
        using var store = new ConnectionStore();

        store.State.Status.ShouldBe(ConnectionStatus.Connecting);
    }

    public static TheoryData<string, Action<ConnectionStore>, ConnectionStatus> Transitions => new()
    {
        { "connecting", store => store.SetConnecting(), ConnectionStatus.Connecting },
        { "live", store => store.SetLive(), ConnectionStatus.Live },
        { "reconnecting", store => store.SetReconnecting(), ConnectionStatus.Reconnecting },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void SetStatus_EachOfTheThreeStates_IsReachable(
        string _, Action<ConnectionStore> transition, ConnectionStatus expected)
    {
        using var store = new ConnectionStore();
        store.SetLive();

        transition(store);

        store.State.Status.ShouldBe(expected);
    }

    public static TheoryData<string, Action<ConnectionStore>> TransitionsThatAreNotBecomingLive => new()
    {
        { "connecting", store => store.SetConnecting() },
        { "reconnecting", store => store.SetReconnecting() },
    };

    [Theory]
    [MemberData(nameof(TransitionsThatAreNotBecomingLive))]
    public void SetStatus_TransitionsThatAreNotBecomingLive_LeaveTheEpochAlone(
        string _, Action<ConnectionStore> transition)
    {
        using var store = new ConnectionStore();
        store.SetLive();

        transition(store);

        store.State.Epoch.ShouldBe(1);
    }
}