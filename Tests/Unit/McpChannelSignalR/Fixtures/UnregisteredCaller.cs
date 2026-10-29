using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// A hub caller that connected and never called RegisterUser: the connection anyone who can reach
// the hub gets for free.
public sealed class UnregisteredCaller : HubCallerContext
{
    public override string ConnectionId => "conn-anon";
    public override string? UserIdentifier => null;
    public override System.Security.Claims.ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}