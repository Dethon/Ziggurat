using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The English line Telegram and WebChat put beside an approval the exec screen raised. Each code
// says its own reason; a request the screen had no part in gets no line at all.
public class ExecScreenReasonsTests
{
    [Theory]
    [InlineData(ExecScreenCodes.NotRequested, "This doesn't look like part of what you asked for.")]
    [InlineData(ExecScreenCodes.Destructive, "It would delete or change something already on your computer.")]
    [InlineData(ExecScreenCodes.SendsOut, "It would send data from your computer to a remote server.")]
    [InlineData(ExecScreenCodes.Unjudged, "It runs on your computer and couldn't be checked first.")]
    public void EachCode_SaysItsReason(string code, string line)
    {
        ExecScreenReasons.English([code]).ShouldBe(line);
    }

    [Fact]
    public void SeveralCodes_AreOneLine_InTheOrderGiven()
    {
        ExecScreenReasons.English([ExecScreenCodes.NotRequested, ExecScreenCodes.SendsOut]).ShouldBe(
            "This doesn't look like part of what you asked for. It would send data from your computer to a remote server.");
    }

    [Fact]
    public void NoCodes_IsNoLine()
    {
        ExecScreenReasons.English(null).ShouldBeNull();
        ExecScreenReasons.English([]).ShouldBeNull();
    }

    // A code from a newer agent than this channel is skipped rather than shown raw.
    [Fact]
    public void AnUnknownCode_IsSkipped()
    {
        ExecScreenReasons.English(["orbit"]).ShouldBeNull();
        ExecScreenReasons.English(["orbit", ExecScreenCodes.Unjudged]).ShouldBe("It runs on your computer and couldn't be checked first.");
    }
}