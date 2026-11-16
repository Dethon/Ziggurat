using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Mcp.Hosting;

// A tool reads its section of the calling household's configuration through the one accessor over
// the verified call (call.Household, Mcp.Hosting/CallHousehold.cs), which proves the household with
// the very token the gate verified. A tool holding the configuration reader itself could ask about
// any household with any proof it came by, and nothing at run time would tell the two apart — only
// the source can (as CallTurnConformanceTests reads it for the turn).
//
// Tool code is a server's tool types and the mounts and tools in Domain/Tools: what a call reaches.
// A server's other parts — an app's request (Sugary's calendar, on its assertion), a fire with no
// call (the scheduling dispatcher, on a schedule's delegation), a channel's startup — build their
// proof through the same accessor's other doors (CallHousehold.OfPerson, OfFire).
public class CallHouseholdConformanceTests
{
    private static readonly Regex _toolCode = new(@"RequestContext\s*<\s*CallToolRequestParams\s*>|\[McpServerTool\b");

    private static readonly Regex _configurationRead = new(@"\bIHouseholdConfiguration\b|\bHouseholdProof\b");

    private static readonly Regex _callTokenProof = new(@"\bHouseholdProof\s*\.\s*OfCallToken\s*\(");

    private static readonly Regex _verifiedToken = new(@"\bVerifiedTokenKey\b");

    private static readonly string _accessor = Path.Combine("Mcp.Hosting", "CallHousehold.cs");

    private static readonly string _turn = Path.Combine("Mcp.Hosting", "CallTurn.cs");

    private static readonly string _gate = Path.Combine("Mcp.Hosting", "CallTokenGate.cs");

    private static readonly string _domainTools = Path.Combine("Domain", "Tools") + Path.DirectorySeparatorChar;

    [Fact]
    public void NoTool_ReadsConfigurationAroundTheAccessor() =>
        Sources()
            .Where(file => file.Key != _accessor)
            .Where(file => file.Key.StartsWith(_domainTools, StringComparison.Ordinal) || _toolCode.IsMatch(file.Value))
            .Where(file => _configurationRead.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty(
                "a tool reads its household's configuration through call.Household (Mcp.Hosting/CallHousehold.cs), "
                + "never with the configuration reader or a proof of its own; these files do");

    [Fact]
    public void NoServer_ProvesAHouseholdWithACallTokenButTheAccessor() =>
        Sources()
            .Where(file => file.Key != _accessor && _callTokenProof.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty(
                "a call token proves a household only as the gate verified it, through call.Household; these files "
                + "build such a proof themselves");

    [Fact]
    public void TheVerifiedToken_IsHandedOnlyFromTheGateToTheAccessor() =>
        Sources()
            .Where(file => file.Key != _turn && file.Key != _gate && _verifiedToken.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty("the token the gate verified is read by the household accessor alone; these files read it");

    // Guards the scans above: a pattern or a root that matched nothing would keep them green forever.
    [Fact]
    public void TheScans_FindTheAccessorAndAToolThatGoesThroughIt()
    {
        var sources = Sources();

        _callTokenProof.IsMatch(sources[_accessor]).ShouldBeTrue($"{_accessor} should build the call token's proof");
        _verifiedToken.IsMatch(sources[_turn]).ShouldBeTrue($"{_turn} should read the token the gate verified");
        var search = sources[Path.Combine("McpServerIdealista", "McpTools", "McpPropertySearchTool.cs")];
        _toolCode.IsMatch(search).ShouldBeTrue("Idealista's search should read as tool code");
        search.ShouldContain("call.Household");
    }

    private static IReadOnlyDictionary<string, string> Sources()
    {
        var root = TestHelpers.FindSolutionRoot();
        return new DirectoryInfo(root)
            .EnumerateDirectories()
            .Where(d => d.Name.StartsWith("McpServer", StringComparison.Ordinal)
                        || d.Name.StartsWith("McpChannel", StringComparison.Ordinal)
                        || d.Name is "Mcp.Hosting" or "Infrastructure" or "Domain")
            .SelectMany(d => d.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f.FullName))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)));
    }
}