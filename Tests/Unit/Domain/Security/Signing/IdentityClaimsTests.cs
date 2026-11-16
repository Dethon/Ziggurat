using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Shouldly;

namespace Tests.Unit.Domain.Security.Signing;

// Who a verified identity is on the channel it names, in the one shape every consumer of a verified
// identity keys by: a person is their account on that channel, the guest is somebody on it with no
// account, and nobody is no principal at all.
public sealed class IdentityClaimsTests
{
    private const string Household = "0123456789abcdef0123456789abcdef";
    private const string Ana = "fedcba9876543210fedcba9876543210";

    [Fact]
    public void APerson_IsTheirAccountOnTheChannelTheIdentityNames()
    {
        IdentityClaims.Person(Ana, Household, ChannelIds.Telegram, ["member"]).Principal
            .ShouldBe(new Principal(ChannelIds.Telegram, Ana, new Account(Household, Ana)));
    }

    [Fact]
    public void TheGuest_IsSomebodyOnTheChannelWithNoAccount()
    {
        IdentityClaims.Guest(Household, ChannelIds.Voice).Principal
            .ShouldBe(new Principal(ChannelIds.Voice, IdentityClaims.GuestSubject));
    }

    [Fact]
    public void Nobody_IsNoPrincipal()
    {
        IdentityClaims.Nobody(ChannelIds.SignalR).Principal.ShouldBeNull();
    }
}