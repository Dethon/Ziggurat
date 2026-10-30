using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Mcp.Hosting;

// Who a call acts for is read in one place, the accessor in Mcp.Hosting, so that swapping what it
// answers — the agent's verified call token instead of the `_meta` the call claims — is one change
// and no tool can keep believing the claim. Every server compiles against the accessor whether or
// not it goes through it, so the only thing that can tell a tool parsing the raw `_meta` itself
// from one that asks the accessor is the source.
//
// Infrastructure/Agents is the agent's side, which writes the meta every call carries; reading it
// there is not a server reading around the accessor.
public class CallTurnConformanceTests
{
    private static readonly Regex _rawTurnRead = new(
        @"\bConversationScope\s*\.\s*(?:Parse|TryResolve)\s*\("
        + @"|\bConversationContextMetaKey\b"
        + @"|\bConversationContextMeta\s*\.\s*MetaKey\b"
        + @"|""com\.herfluffness/conversationContext""");

    private static readonly string _accessor = Path.Combine("Mcp.Hosting", "CallTurn.cs");

    private static readonly string _agentSide = Path.Combine("Infrastructure", "Agents") + Path.DirectorySeparatorChar;

    [Fact]
    public void NoServer_ReadsTheTurnOffTheRawMeta() =>
        ServerSources()
            .Where(file => file.Key != _accessor && _rawTurnRead.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty(
                "a server reads who a call acts for through the accessor in Mcp.Hosting (call.Turn, "
                + "call.TryScope), never by parsing the call's _meta itself; these files do");

    // Guards the scan above: a pattern or a root that matched nothing would keep it green forever.
    [Fact]
    public void TheAccessor_IsWhereTheTurnIsRead() =>
        _rawTurnRead.IsMatch(ServerSources()[_accessor]).ShouldBeTrue(
            $"{_accessor} should be the one place that reads the turn off a call's _meta");

    private static IReadOnlyDictionary<string, string> ServerSources()
    {
        var root = TestHelpers.FindSolutionRoot();
        return new DirectoryInfo(root)
            .EnumerateDirectories()
            .Where(d => d.Name.StartsWith("McpServer", StringComparison.Ordinal)
                        || d.Name.StartsWith("McpChannel", StringComparison.Ordinal)
                        || d.Name is "Mcp.Hosting" or "Infrastructure")
            .SelectMany(d => d.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f.FullName))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => !path.StartsWith(_agentSide, StringComparison.Ordinal))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)));
    }
}