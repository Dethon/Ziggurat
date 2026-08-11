using Dashboard.Client.State.Connection;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class ConnectionStoreTests
{
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