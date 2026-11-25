using McpServerApps.Serving;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// A page's inline import maps, as the policy pins them: the SHA-256 of each map's text as the browser
// parsed it (line ends made LF), so the map runs and no other inline script does.
public class InlineImportMapsTests
{
    [Fact]
    public void EachImportMap_IsHashedAsTheBrowserReadsIt()
    {
        var html = "<html><head>\r\n<script type=\"importmap\">\r\n{ \"imports\": { \"chart\": \"/lib/chart.js\" } }\r\n</script>"
                   + "<script type=\"importmap\">{\"imports\":{}}</script></head></html>";

        InlineImportMaps.Hashes(html).ShouldBe([
            "sha256-r+N6Yf1SeJsNgsY24jUiUOumEkuP08+oi2HOAR51URs=",
            "sha256-URrTy+Il/Nz0lHojVUx275hWqAWhkSF0VsHbUM4/6Hw="
        ]);
    }

    [Fact]
    public void AnyOtherInlineScript_IsNotHashed() =>
        InlineImportMaps.Hashes("""<script>alert(1)</script><script type="module">import "x"</script><script src="/a.js"></script>""")
            .ShouldBeEmpty();
}