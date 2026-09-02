using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The command line an action file receives is written by a model, and the one shape it writes for
// a JSON argument is a double-quoted string with the inner quotes escaped — the shape every shell
// accepts. A tokeniser that drops the backslash-escaped quotes hands Home Assistant `{\target\:…}`,
// which is what a calendar held after a real turn: an alarm whose target nothing could parse.
public class HaFileSystemExecQuotingTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("effect", new HaServiceField()),
                ("note", new HaServiceField())) }
        };
        var local = client;
        return new HaFileSystem(new HaCatalogProvider(() => local, new FakeTimeProvider()), () => local);
    }

    private static async Task<string> Argument(HaFileSystem fs, FakeHaClient client, string command, string name)
    {
        var result = await fs.ExecAsync("entities/light/kitchen", command, null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        return client.LastCall!.Value.Data![name]!.GetValue<string>();
    }

    [Fact]
    public async Task Exec_EscapedQuotesInsideDoubleQuotes_ReachTheServiceAsQuotes()
    {
        var fs = Build(out var client);

        var value = await Argument(fs, client,
            """turn_on.sh --effect "{\"target\":{\"room\":\"Kitchen\"}}" """, "effect");

        value.ShouldBe("""{"target":{"room":"Kitchen"}}""");
        JsonNode.Parse(value)!["target"]!["room"]!.GetValue<string>().ShouldBe("Kitchen");
    }

    [Fact]
    public async Task Exec_EscapedBackslashInsideDoubleQuotes_IsOneBackslash()
    {
        var fs = Build(out var client);

        var value = await Argument(fs, client, """turn_on.sh --note "C:\\temp" """, "note");

        value.ShouldBe(@"C:\temp");
    }

    // Bash keeps the backslash before a character it does not escape inside double quotes, so a
    // Windows path or a regex written that way survives untouched.
    [Fact]
    public async Task Exec_BackslashBeforeAnOrdinaryCharacter_IsKept()
    {
        var fs = Build(out var client);

        var value = await Argument(fs, client, """turn_on.sh --note "a\nb" """, "note");

        value.ShouldBe(@"a\nb");
    }

    [Fact]
    public async Task Exec_SingleQuotes_KeepEverythingLiteral()
    {
        var fs = Build(out var client);

        var value = await Argument(fs, client, """turn_on.sh --effect '{"a":"b\"c"}' """, "effect");

        value.ShouldBe("""{"a":"b\"c"}""");
    }

    [Fact]
    public async Task Exec_UnquotedBackslash_EscapesTheNextCharacter()
    {
        var fs = Build(out var client);

        var value = await Argument(fs, client, """turn_on.sh --note two\ words""", "note");

        value.ShouldBe("two words");
    }
}