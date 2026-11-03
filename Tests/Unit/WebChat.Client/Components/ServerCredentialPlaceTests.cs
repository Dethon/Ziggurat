using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;
using WebChat.Client.Components.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// Where a freshly minted tunnel credential goes, named exactly: the kit reads one variable per server
// (TUNNEL_CREDENTIAL_VOICE, not "TUNNEL_CREDENTIAL for it"), a machine running the outpost reads
// CREDENTIAL, and a server the kit does not run is named no kit variable that does not exist.
public sealed class ServerCredentialPlaceTests
{
    [Theory]
    [InlineData("voice", "the tenant kit's TUNNEL_CREDENTIAL_VOICE in its .env")]
    [InlineData("homeassistant", "the tenant kit's TUNNEL_CREDENTIAL_HOMEASSISTANT in its .env")]
    [InlineData("outpost-laptop", "the machine's CREDENTIAL when it runs the outpost")]
    [InlineData("my-server", "its own tunnel credential (MCP__TUNNEL__CREDENTIAL)")]
    public void ACredential_IsPutWhereItsServerReadsIt(string server, string place) =>
        ServerCredentialPlace.For(server).ShouldBe(place);

    [Fact]
    public void TheKitsServers_AreTheOnesItsEnvNamesACredentialFor()
    {
        var env = File.ReadAllText(Path.Combine(TestHelpers.FindSolutionRoot(), "TenantKit", ".env"));

        var named = Regex.Matches(env, @"^TUNNEL_CREDENTIAL_([A-Z]+)=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.ToLowerInvariant());

        named.ShouldBe(ServerCredentialPlace.KitServers, ignoreOrder: true);
    }

    // The Servers page named no server: Chidi, a new owner, was asked for "the name the server runs
    // under" with nothing to say which names the kit's servers take or what each gives the agents.
    [Fact]
    public void EveryKitServer_IsSaidWhatItGivesTheAgents()
    {
        ServerCredentialPlace.KitServers.ShouldAllBe(server => !string.IsNullOrWhiteSpace(ServerCredentialPlace.WhatItGives(server)));
        ServerCredentialPlace.WhatItGives("my-server").ShouldBeNull();
    }
}