using Shouldly;
using WebChat.Client.Components.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// Where a freshly minted tunnel credential goes, named exactly: the kit reads one variable per server
// (TUNNEL_CREDENTIAL_VOICE, not "TUNNEL_CREDENTIAL for it"), and a machine running the outpost reads
// CREDENTIAL.
public sealed class ServerCredentialPlaceTests
{
    [Theory]
    [InlineData("voice", "the tenant kit's TUNNEL_CREDENTIAL_VOICE in its .env")]
    [InlineData("homeassistant", "the tenant kit's TUNNEL_CREDENTIAL_HOMEASSISTANT in its .env")]
    [InlineData("my-server", "the tenant kit's TUNNEL_CREDENTIAL_MY_SERVER in its .env")]
    [InlineData("outpost-laptop", "the machine's CREDENTIAL when it runs the outpost")]
    public void ACredential_IsPutWhereItsServerReadsIt(string server, string place) =>
        ServerCredentialPlace.For(server).ShouldBe(place);
}