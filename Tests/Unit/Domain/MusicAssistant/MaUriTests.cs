using Domain.Tools.MusicAssistant;
using Shouldly;

namespace Tests.Unit.Domain.MusicAssistant;

public class MaUriTests
{
    [Fact]
    public void TryParse_ProviderInstanceUri_SplitsAllThreeParts()
    {
        MaUri.TryParse("spotify--w2nq2jMe://podcast/5dbvpKwtqz3X3hcX1BSEzf", out var uri).ShouldBeTrue();

        uri.Provider.ShouldBe("spotify--w2nq2jMe");
        uri.MediaType.ShouldBe("podcast");
        uri.ItemId.ShouldBe("5dbvpKwtqz3X3hcX1BSEzf");
    }

    // A bare provider domain (no `--<instance>` suffix) is what MA returns when only one instance
    // of a provider is configured; both forms have to round-trip.
    [Fact]
    public void TryParse_BareProviderDomain_Parses()
    {
        MaUri.TryParse("library://podcast/12", out var uri).ShouldBeTrue();

        uri.Provider.ShouldBe("library");
        uri.ItemId.ShouldBe("12");
    }

    // Item ids can themselves contain slashes (filesystem provider paths); only the first two
    // separators are structural.
    [Fact]
    public void TryParse_ItemIdContainingSlashes_KeepsRemainderIntact()
    {
        MaUri.TryParse("filesystem_local--x1://track/Music/Album/01.flac", out var uri).ShouldBeTrue();

        uri.MediaType.ShouldBe("track");
        uri.ItemId.ShouldBe("Music/Album/01.flac");
    }

    [Theory]
    [InlineData("")]
    [InlineData("No es el fin del mundo")]
    [InlineData("spotify:episode:4Fk1sWv0xKvJ6teiCpTAJN")]
    [InlineData("spotify--w2nq2jMe://podcast")]
    [InlineData("://podcast/5dbv")]
    [InlineData("spotify--w2nq2jMe://podcast/")]
    public void TryParse_NotAnMaUri_ReturnsFalse(string candidate)
    {
        MaUri.TryParse(candidate, out _).ShouldBeFalse();
    }

    // The agent passes a free-text podcast name OR a URI to the same argument, so the VFS action
    // needs a cheap discriminator that does not misread a title containing a colon.
    [Fact]
    public void LooksLikeUri_DistinguishesUriFromTitle()
    {
        MaUri.LooksLikeUri("spotify--w2nq2jMe://podcast/5dbv").ShouldBeTrue();
        MaUri.LooksLikeUri("280. Palantir: el control tecnológico de la defensa").ShouldBeFalse();
        MaUri.LooksLikeUri("spotify:episode:4Fk1sWv0xKvJ6teiCpTAJN").ShouldBeFalse();
    }
}