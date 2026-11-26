using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// A built app's app.json, judged whole before anything is stored: every rule it breaks is named in
// the rule's own words, so the agent fixes them in one go.
public class AppManifestTests
{
    private const string Valid = """
                                 {
                                   "name": "Glucose today",
                                   "shortName": "Glucose",
                                   "accentColor": "#e9601f",
                                   "description": "My glucose today beside what I ate.",
                                   "root": "dist",
                                   "reads": ["/sugary", "/ha"],
                                   "connect": ["https://api.example.com", "https://tiles.example.org:8443"],
                                   "icon": "/icons/glucose.svg",
                                   "instructions": "asks/instructions.md",
                                   "asks": ["vault", "sugary", "homeassistant"],
                                   "model": "z-ai/glm-5.3-flash"
                                 }
                                 """;

    [Fact]
    public void AWellFormedManifest_IsReadWhole()
    {
        var manifest = AppManifest.Parse(Valid).ShouldBeOfType<AppManifestParse.Valid>().Manifest;

        manifest.Name.ShouldBe("Glucose today");
        manifest.ShortName.ShouldBe("Glucose");
        manifest.AccentColor.ShouldBe("#e9601f");
        manifest.Root.ShouldBe("dist");
        manifest.Reads.ShouldBe(["/sugary", "/ha"]);
        manifest.Connect.ShouldBe(["https://api.example.com", "https://tiles.example.org:8443"]);
        manifest.Icon.ShouldBe("/icons/glucose.svg");
        manifest.Instructions.ShouldBe("asks/instructions.md");
        manifest.Asks.ShouldBe(["vault", "sugary", "homeassistant"]);
        manifest.Model.ShouldBe("z-ai/glm-5.3-flash");
    }

    [Fact]
    public void TheOptionalFields_DefaultToTheRepositoryRootAndNothingReadOrFetched()
    {
        var manifest = AppManifest.Parse("""{"name":"A","accentColor":"#abc","description":"B"}""")
            .ShouldBeOfType<AppManifestParse.Valid>().Manifest;

        manifest.ShortName.ShouldBeNull();
        manifest.Root.ShouldBe("");
        manifest.Reads.ShouldBeEmpty();
        manifest.Connect.ShouldBeEmpty();
        manifest.Icon.ShouldBeNull();
        manifest.Instructions.ShouldBeNull();
        manifest.Asks.ShouldBeEmpty();
        manifest.Model.ShouldBeNull();
    }

    // A model is named as the agent's patchable models name it: a provider's id or a local box's, kept as
    // written, since whether it is one of the building agent's is the agent's to say at each ask.
    [Theory]
    [InlineData("openai/gpt-6-luna")]
    [InlineData("deepseek/deepseek-v4.1-flash-1017")]
    [InlineData("meta-llama/llama-4-scout:free")]
    [InlineData("lemonade/Qwen3-8B-GGUF")]
    [InlineData("lemonade/user.Gemma-3-4b-it")]
    public void AModel_IsKeptAsWritten(string model) =>
        AppManifest.Parse($$"""{"name":"A","accentColor":"#abc","description":"B","model":"{{model}}"}""")
            .ShouldBeOfType<AppManifestParse.Valid>().Manifest.Model.ShouldBe(model);

    [Theory]
    [InlineData("""{"accentColor":"#abc","description":"B"}""", "name is required")]
    [InlineData("""{"name":"A","description":"B"}""", "accentColor")]
    [InlineData("""{"name":"A","accentColor":"orange","description":"B"}""", "#rgb, #rrggbb or #rrggbbaa")]
    [InlineData("""{"name":"A","accentColor":"#abc"}""", "description is required")]
    [InlineData("""{"name":"A","shortName":"Much too long a name","accentColor":"#abc","description":"B"}""", "shortName")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","root":"/dist"}""", "root")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","root":"../elsewhere"}""", "root")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","root":"node_modules/x"}""", "root")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","reads":["sugary"]}""", "'sugary' is no mount point")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","reads":["/sugary/glucose"]}""", "'/sugary/glucose' is no mount point")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","reads":["/sugary","/sugary"]}""", "'/sugary' is named twice")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","connect":["http://api.example.com"]}""", "'http://api.example.com' is no bare https origin")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","connect":["https://*.example.com"]}""", "'https://*.example.com' is no bare https origin")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","connect":["https://api.example.com/v1"]}""", "'https://api.example.com/v1' is no bare https origin")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","connect":["https://api.example.com/"]}""", "'https://api.example.com/' is no bare https origin")]
    // An icon is an SVG the origin serves, named by its path there; /_app/ is the server's own.
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","icon":"icon.svg"}""", "icon 'icon.svg'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","icon":"/icon.png"}""", "icon '/icon.png'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","icon":"/../icon.svg"}""", "icon '/../icon.svg'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","icon":"/_app/icon.svg"}""", "icon '/_app/icon.svg'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","icon":"https://example.org/i.svg"}""", "icon 'https://example.org/i.svg'")]
    // The instructions are a markdown file of the repository, named from its root, wherever the app serves.
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"/instructions.md"}""", "instructions '/instructions.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"instructions.txt"}""", "instructions 'instructions.txt'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"../instructions.md"}""", "instructions '../instructions.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"asks/./a.md"}""", "instructions 'asks/./a.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"asks//a.md"}""", "instructions 'asks//a.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"asks\\a.md"}""", "instructions 'asks\\a.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":".git/a.md"}""", "instructions '.git/a.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":"node_modules/x/README.md"}""", "instructions 'node_modules/x/README.md'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":""}""", "instructions ''")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","instructions":7}""", "instructions must be a string")]
    // The servers an ask may use, by the name each is configured under (a shared server's `sharedServers`
    // key, a household server's own name), each named once: never a tool's qualifier, a host or an address.
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["mcp__vault"]}""", "asks: 'mcp__vault' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["Vault"]}""", "asks: 'Vault' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["http://mcp-vault:8080/mcp"]}""", "asks: 'http://mcp-vault:8080/mcp' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["-vault"]}""", "asks: '-vault' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["a..b"]}""", "asks: 'a..b' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["tools.example.org"]}""", "asks: 'tools.example.org' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":[""]}""", "asks: '' is no server's name")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":["vault","vault"]}""", "asks: 'vault' is named twice")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","asks":"vault"}""", "asks must be a list of strings")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":"glm-5.3"}""", "model 'glm-5.3'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":"/glm-5.3"}""", "model '/glm-5.3'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":"z-ai/"}""", "model 'z-ai/'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":"z-ai/glm 5.3"}""", "model 'z-ai/glm 5.3'")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":""}""", "model ''")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","model":7}""", "model must be a string")]
    [InlineData("""{"name":"A","accentColor":"#abc","description":"B","colour":"#abc"}""", "colour")]
    [InlineData("""[1, 2]""", "a JSON object")]
    [InlineData("""{"name":""", "not JSON")]
    public void ABrokenRule_IsRefusedInItsOwnWords(string json, string words)
    {
        var refused = AppManifest.Parse(json).ShouldBeOfType<AppManifestParse.Refused>();

        refused.Problems.ShouldContain(problem => problem.Contains(words, StringComparison.Ordinal));
    }

    [Fact]
    public void AModelOverItsLength_IsRefused() =>
        AppManifest.Parse($$"""{"name":"A","accentColor":"#abc","description":"B","model":"z-ai/{{new string('g', AppManifest.ModelLength)}}"}""")
            .ShouldBeOfType<AppManifestParse.Refused>().Problems.ShouldHaveSingleItem().ShouldContain("model");

    [Fact]
    public void EveryBrokenRule_IsNamed_NotOnlyTheFirst()
    {
        var refused = AppManifest.Parse("""{"accentColor":"orange","reads":["x"],"connect":["ftp://a"]}""")
            .ShouldBeOfType<AppManifestParse.Refused>();

        refused.Problems.Count.ShouldBe(5);
    }
}