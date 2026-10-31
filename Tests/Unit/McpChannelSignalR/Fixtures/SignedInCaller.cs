using Domain.Security;
using McpChannelSignalR.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// A hub caller the proxy named as it arrived, which is what every hub method needs before doing
// anything. Shared because every hub test needs one and they all need the same one.
public sealed class SignedInCaller : HubCallerContext
{
    public override string ConnectionId => "conn-1";
    public override string? UserIdentifier => "fran";
    public override System.Security.Claims.ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } =
        new Dictionary<object, object?>
        {
            [ChatHub.AccountItem] = new ForwardedAccount("fran", "home", ["owner"], SessionId: "session-1")
        };
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}