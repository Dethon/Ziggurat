using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public sealed class WebManifestNamingTests
{
    [Fact]
    public void Resolve_NoMatchingSpace_ShortNameFallsBackToBaseName()
    {
        var (_, shortName) = WebManifestNaming.Resolve(null);

        shortName.ShouldBe(WebManifestNaming.BaseName);
    }

    [Fact]
    public void Resolve_NamedSpace_NameAppendsSpaceAndShortNameIsSpaceName()
    {
        var space = new SpaceConfig("work", "Work", SpaceConfig.DefaultAccentColor);

        var (name, shortName) = WebManifestNaming.Resolve(space);

        name.ShouldBe("Herfluffness' Assistants — Work");
        shortName.ShouldBe("Work");
    }

    [Fact]
    public void Resolve_DefaultSpace_NameAndShortNameAreBaseName()
    {
        var space = new SpaceConfig("default", "Main", SpaceConfig.DefaultAccentColor);

        var (name, shortName) = WebManifestNaming.Resolve(space);

        name.ShouldBe("Herfluffness' Assistants");
        shortName.ShouldBe("Herfluffness' Assistants");
    }
}