using McpServerApps.Publishing;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// Code and styles a built app loads from outside are pinned: an exact version in the address and the
// integrity hash of those bytes, so what the person runs is what the agent checked, whatever the CDN
// later serves at that address. An import map names an integrity entry for every outside module.
public class OutsideCodePinningTests
{
    private const string Hash = "sha384-oqVuAfXRKap7fdgcCY5uykM6+R9GqQ8K/uxy9rx7HNQlGYl1kPzQho1wx4JwY8wC";

    private static IReadOnlyList<string> Check(string html) => OutsideCodePinning.Problems("index.html", html);

    [Theory]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js" integrity="{Hash}" crossorigin="anonymous"></script>""")]
    [InlineData($"""<script type="module" src="https://esm.sh/preact@10.19.3" integrity="{Hash}"></script>""")]
    [InlineData($"""<link rel="stylesheet" href="https://unpkg.com/pico.css@2.0.6/css/pico.min.css" integrity="{Hash}">""")]
    [InlineData($"""<script src="https://cdnjs.cloudflare.com/ajax/libs/d3/7.8.5/d3.min.js" integrity="{Hash}"></script>""")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/lit@3.1.0-next.1/index.js" integrity="{Hash}"></script>""")]
    [InlineData("""<script src="app.js"></script><script src="/vendor/chart.js"></script><link rel="stylesheet" href="./style.css">""")]
    [InlineData("""<link rel="icon" href="https://example.org/favicon.ico"><a href="https://example.org/">a link</a>""")]
    [InlineData("""<script>console.log("inline")</script>""")]
    public void PinnedOrOwnCode_Passes(string body) => Check(Page(body)).ShouldBeEmpty();

    [Theory]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>""", "integrity")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js/dist/chart.umd.min.js" integrity="{Hash}"></script>""", "exact version")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js" integrity="{Hash}"></script>""", "exact version")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4/dist/chart.umd.min.js" integrity="{Hash}"></script>""", "exact version")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js@latest/dist/chart.umd.min.js" integrity="{Hash}"></script>""", "exact version")]
    [InlineData($"""<script src="https://cdn.jsdelivr.net/npm/chart.js@^4.4.1/dist/chart.umd.min.js" integrity="{Hash}"></script>""", "exact version")]
    [InlineData("""<script src="//cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>""", "integrity")]
    [InlineData("""<link rel="stylesheet" href="https://unpkg.com/pico.css@2.0.6/css/pico.min.css">""", "integrity")]
    [InlineData("""<link rel="modulepreload" href="https://esm.sh/preact@10.19.3">""", "integrity")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js" integrity=""></script>""", "integrity")]
    public void UnpinnedOutsideCode_IsRefused_SayingWhatIsMissing(string body, string missing)
    {
        var problems = Check(Page(body));

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("index.html");
        problems[0].ShouldContain(missing);
    }

    [Fact]
    public void EveryUnpinnedLoad_IsNamed_NotJustTheFirst()
    {
        var problems = Check(Page("""
            <script src="https://esm.sh/a@1.0.0"></script>
            <script src="https://esm.sh/b@1.0.0"></script>
            """));

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.Contains("https://esm.sh/a@1.0.0"));
        problems.ShouldContain(p => p.Contains("https://esm.sh/b@1.0.0"));
    }

    [Fact]
    public void AnImportMap_WhoseOutsideModulesAllHaveIntegrity_Passes() =>
        Check(Page($$"""
            <script type="importmap">
            {
              "imports": { "preact": "https://esm.sh/preact@10.19.3", "./util": "./util.js" },
              "integrity": { "https://esm.sh/preact@10.19.3": "{{Hash}}" }
            }
            </script>
            """)).ShouldBeEmpty();

    [Theory]
    [InlineData("""{ "imports": { "preact": "https://esm.sh/preact@10.19.3" } }""", "https://esm.sh/preact@10.19.3")]
    [InlineData("""{ "imports": { "preact": "https://esm.sh/preact@10.19.3" }, "integrity": { "https://esm.sh/other@1.0.0": "sha384-x" } }""", "https://esm.sh/preact@10.19.3")]
    [InlineData("""{ "scopes": { "/admin/": { "chart": "https://esm.sh/chart.js@4.4.1" } } }""", "https://esm.sh/chart.js@4.4.1")]
    // A prefix mapping lets any module under it load, and no integrity entry can name them all.
    [InlineData("""{ "imports": { "lodash/": "https://esm.sh/lodash-es@4.17.21/" }, "integrity": { "https://esm.sh/lodash-es@4.17.21/": "sha384-x" } }""", "https://esm.sh/lodash-es@4.17.21/")]
    public void AnImportMap_NamingAnOutsideModuleWithoutIntegrity_IsRefused(string map, string module)
    {
        var problems = Check(Page($"""<script type="importmap">{map}</script>"""));

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain(module);
        problems[0].ShouldContain("import map");
    }

    [Fact]
    public void AnImportMapThatIsNotJson_IsRefused() =>
        Check(Page("""<script type="importmap">{ imports: </script>""")).ShouldHaveSingleItem().ShouldContain("import map");

    private static string Page(string body) =>
        $"<!doctype html><html><head><title>Glucose</title>{body}</head><body></body></html>";
}