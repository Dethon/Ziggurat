using Shouldly;
using WebChat.Client.Helpers;

namespace Tests.Unit.WebChat.Client.Helpers;

public sealed class AvatarHelperTests
{
    [Fact]
    public void GetInitials_TwoWords_ReturnsTwoUppercaseInitials()
    {
        AvatarHelper.GetInitials("Ada Lovelace").ShouldBe("AL");
    }
}