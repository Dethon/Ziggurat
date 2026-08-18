using System.Reflection;
using System.Text.Json.Nodes;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

// The taxonomy is a contract with a model: the same failure has to read the same way from every
// tool, or an agent learns that a dependency being down is sometimes worth waiting for and
// sometimes not. These pin the three promises the envelope makes — the code is one of a known set,
// its retryability comes from the code, and a failure with a recovery action always names one.
public class ToolErrorTaxonomyTests
{
    private static IReadOnlyList<string> DeclaredCodes =>
        [.. typeof(ToolError.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)];

    public static TheoryData<string> Codes => [.. DeclaredCodes];

    // Codes worth waiting out: something outside this process was not ready, and the same call may
    // land next time. Everything else is a decision the caller has to change.
    private static readonly string[] _retryable =
    [
        ToolError.Codes.Timeout,
        ToolError.Codes.TransientDependency,
        ToolError.Codes.RateLimited,
        ToolError.Codes.InternalError
    ];

    // Codes where "no" on its own would send a model round the same loop in different words.
    private static readonly string[] _mustSayWhatToDo =
    [
        ToolError.Codes.UnsupportedOperation,
        ToolError.Codes.PermissionDenied,
        ToolError.Codes.Authentication,
        ToolError.Codes.RateLimited,
        ToolError.Codes.TransientDependency,
        ToolError.Codes.PartialSuccess,
        ToolError.Codes.CaptchaRequired
    ];

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryDeclaredCode_IsInTheTaxonomy(string code)
    {
        ToolError.IsKnown(code).ShouldBeTrue($"'{code}' is a const nobody gave a meaning to");
    }

    [Fact]
    public void TheTaxonomy_DeclaresNothingThatIsNotAConst()
    {
        ToolError.All.ShouldBe(DeclaredCodes, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void RetryabilityComesFromTheCode_AndOnlyTheTransientOnesInviteARetry(string code)
    {
        ToolError.IsRetryable(code).ShouldBe(_retryable.Contains(code), code);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void AFailureWithARecoveryAction_AlwaysNamesOneEvenWhenTheCallSiteDidNot(string code)
    {
        var bare = ToolError.Create(code, "something went wrong");

        if (_mustSayWhatToDo.Contains(code))
        {
            bare["hint"].ShouldNotBeNull($"'{code}' must say what to do instead");
            bare["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        }
        else
        {
            // Not silence for its own sake: a hint that repeats the message teaches the model
            // nothing and costs a line of every failure.
            bare["hint"].ShouldBeNull($"'{code}' invented a recovery action nobody wrote");
        }
    }

    [Fact]
    public void ASiteWithSomethingSpecificToSay_KeepsItsOwnHint()
    {
        ToolError.Create(ToolError.Codes.PermissionDenied, "no", "ask the user for the vault password")
            ["hint"]!.GetValue<string>().ShouldBe("ask the user for the vault password");
    }

    [Fact]
    public void AnUnknownCode_IsNotRetryable()
    {
        // A code from outside this taxonomy — a third-party server, a later version — is a failure
        // nothing here understands, and inviting a loop on one is the worse of the two guesses.
        ToolError.IsRetryable("something_else_entirely").ShouldBeFalse();
        ToolError.Create("something_else_entirely", "x")["retryable"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void TheEnvelope_CarriesTheFourFieldsEveryConsumerReads()
    {
        var envelope = ToolError.Create(ToolError.Codes.Timeout, "the call took too long");

        envelope["ok"]!.GetValue<bool>().ShouldBeFalse();
        envelope["errorCode"]!.GetValue<string>().ShouldBe("timeout");
        envelope["message"]!.GetValue<string>().ShouldBe("the call took too long");
        envelope["retryable"]!.GetValue<bool>().ShouldBeTrue();
    }

    // An envelope crossing a server boundary is re-read by this side's taxonomy, so a foreign
    // server cannot claim a retryability this side disagrees with for the same code.
    [Fact]
    public void ReadingAnEnvelopeBack_AnswersRetryabilityFromTheCodeRatherThanTheWire()
    {
        var wire = new JsonObject
        {
            ["ok"] = false,
            ["errorCode"] = ToolError.Codes.InvalidArgument,
            ["message"] = "no",
            ["retryable"] = true
        };

        ToolErrorResult.FromEnvelope(wire)!.Retryable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(CapabilityState.Absent, ToolError.Codes.NotFound, false)]
    [InlineData(CapabilityState.Unavailable, ToolError.Codes.TransientDependency, true)]
    [InlineData(CapabilityState.Unassigned, ToolError.Codes.PermissionDenied, false)]
    [InlineData(CapabilityState.Unsupported, ToolError.Codes.UnsupportedOperation, false)]
    public void EachCapabilityState_AnswersAsTheCodeACallerWouldActOnTheSameWay(
        CapabilityState state, string code, bool retryable)
    {
        var error = CapabilityError.For(state, "the machine is not here", "use the vault instead");

        error.ErrorCode.ShouldBe(code);
        error.Retryable.ShouldBe(retryable);
        error.Recovery.ShouldBe("use the vault instead");
    }

    // The distinction the four states exist for: only one of them is worth trying again, and a
    // single "unavailable" for all four is what made a model retry the three that never clear.
    [Fact]
    public void OnlyTheUnavailableState_InvitesARetry()
    {
        Enum.GetValues<CapabilityState>()
            .Where(state => CapabilityError.For(state, "x", "y").Retryable)
            .ShouldBe([CapabilityState.Unavailable]);
    }
}