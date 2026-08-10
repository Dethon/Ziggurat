using Domain.Channels;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class TurnKeyTests
{
    // Voice and the conversation group mint in different processes and compare the results as
    // opaque strings; this pins the one spelling they must agree on.
    [Fact]
    public void MintedKey_IsThirtyTwoLowercaseHexCharacters()
    {
        TurnKey.Mint().ShouldMatch("^[0-9a-f]{32}$");
    }
}