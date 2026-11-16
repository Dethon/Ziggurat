using System.Buffers.Binary;
using Observability;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Observability;

// Android Chrome installs the dashboard from a raster icon: with only an SVG to offer, the install
// dialog came up without an icon and its button did nothing. So the manifest names PNGs at the
// sizes Android asks for, and each one is a file of the web root at the size it claims.
public class DashboardManifestTests
{
    [Theory]
    [InlineData(192)]
    [InlineData(512)]
    public void Manifest_OffersAPngIconAtTheSizeAndroidInstallsFrom(int size)
    {
        DashboardManifest.Icons.ShouldContain(
            i => i.Type == "image/png" && i.Sizes == $"{size}x{size}",
            $"no {size}x{size} PNG in the dashboard's manifest");
    }

    [Fact]
    public void EveryPngIcon_IsAFileOfTheWebRootAtTheSizeItClaims()
    {
        var webRoot = Path.Combine(TestHelpers.FindSolutionRoot(), "Dashboard.Client", "wwwroot");

        foreach (var icon in DashboardManifest.Icons.Where(i => i.Type == "image/png"))
        {
            var file = Path.Combine(webRoot, icon.Src);
            File.Exists(file).ShouldBeTrue($"{icon.Src} is not in Dashboard.Client/wwwroot");

            var header = File.ReadAllBytes(file).AsSpan(0, 24);
            var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            $"{width}x{height}".ShouldBe(icon.Sizes, $"{icon.Src} is not the size the manifest says");
        }
    }
}