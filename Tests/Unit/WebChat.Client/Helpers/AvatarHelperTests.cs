using Shouldly;
using WebChat.Client.Helpers;

namespace Tests.Unit.WebChat.Client.Helpers;

public sealed class AvatarHelperTests
{
    private static readonly string[] _warmPalette =
    [
        "#E9601F", "#C2693B", "#B5611F", "#A8743A",
        "#9A5B4A", "#7C6A3F", "#3F7D6E", "#8A5A3C"
    ];

    [Fact]
    public void GetColorForUser_AlwaysReturnsAWarmPaletteColor()
    {
        var seeds = Enumerable.Range(0, 60).Select(i => $"agent-{i}");
        seeds.Select(AvatarHelper.GetColorForUser)
            .ShouldAllBe(color => _warmPalette.Contains(color));
    }

    [Fact]
    public void GetInitials_TwoWords_ReturnsTwoUppercaseInitials()
    {
        AvatarHelper.GetInitials("Ada Lovelace").ShouldBe("AL");
    }
}