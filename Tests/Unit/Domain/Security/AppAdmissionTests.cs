using Domain.DTOs.Channel;
using Domain.Security;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace Tests.Unit.Domain.Security;

// Who an app behind the proxy lets in, decided once for all five (the chat hub, Sugary's API, the
// dashboard, the agent registration API, the admin API): signed out without one assertion the
// accounts service signed for the app's channel naming a person; forbidden for a person lacking what
// the app asks; otherwise the person, with the very assertion, which an app may hand on. Each app
// only says how it answers each — a hub exception, a 401, its own refusal's body.
public sealed class AppAdmissionTests
{
    private static readonly Account _ana = new("0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210");
    private const string Channel = AppChannels.Dashboard;

    private static Dictionary<string, StringValues> Headers(params string[] assertions) =>
        new() { [ForwardedAccount.AssertionHeader] = new StringValues(assertions) };

    private static Task<AppAdmission> AdmitAsync(Dictionary<string, StringValues> headers, Func<ForwardedAccount, bool>? may = null) =>
        TestAssertions.Verifier().AdmitAsync(headers, Channel, may, CancellationToken.None);

    [Fact]
    public async Task APersonsAssertionForTheAppsChannel_AdmitsThem_WithTheAssertion()
    {
        var assertion = TestAssertions.Of(_ana, Channel, ["owner"]);

        var admitted = (await AdmitAsync(Headers(assertion))).ShouldBeOfType<AppAdmission.Admitted>();

        (admitted.Person.TenantId, admitted.Person.UserId).ShouldBe((_ana.TenantId, _ana.UserId));
        admitted.Assertion.ShouldBe(assertion);
    }

    public static TheoryData<string> SignedOut => ["none", "two", "forged", "another channel", "the guest"];

    [Theory]
    [MemberData(nameof(SignedOut))]
    public async Task ARequestWithNoBelievableAssertion_IsSignedOut(string what)
    {
        var headers = what switch
        {
            "none" => new Dictionary<string, StringValues>(),
            "two" => Headers(TestAssertions.Of(_ana, Channel), TestAssertions.Of(_ana, Channel)),
            "forged" => Headers(TestAssertions.Forged(_ana, Channel)),
            "another channel" => Headers(TestAssertions.Of(_ana, AppChannels.SexyTime)),
            "the guest" => Headers(TestAssertions.OfGuest(_ana.TenantId, Channel)),
            _ => throw new ArgumentOutOfRangeException(nameof(what))
        };

        var admission = await AdmitAsync(headers);

        admission.ShouldBeOfType<AppAdmission.SignedOut>(what);
        admission.Status.ShouldBe(401);
    }

    [Fact]
    public async Task APersonLackingWhatTheAppAsks_IsForbidden_AndNamed()
    {
        var admission = await AdmitAsync(Headers(TestAssertions.Of(_ana, Channel, ["member"])), person => person.IsOperator);

        admission.ShouldBeOfType<AppAdmission.Forbidden>().Person.UserId.ShouldBe(_ana.UserId);
        admission.Status.ShouldBe(403);
    }
}