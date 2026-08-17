using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Outposts;

// What the hub refuses to store rather than hold as an entry nobody can ever act on: a blank name
// has no keepalive route and no mount, and an endpoint that is not an absolute URL is nothing a
// session build could dial.
public class OutpostRegistrationTests
{
    [Fact]
    public void ANamedMachineWithADialableEndpoint_IsRegistrable()
    {
        Registration("laptop", "http://192.168.1.20:8099/mcp").Registrable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankName_IsNot(string name)
    {
        Registration(name, "http://192.168.1.20:8099/mcp").Registrable.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/mcp")]
    public void AnEndpointNothingCouldDial_IsNot(string endpoint)
    {
        Registration("laptop", endpoint).Registrable.ShouldBeFalse();
    }

    private static OutpostRegistration Registration(string name, string endpoint) =>
        new() { Name = name, Endpoint = endpoint };
}