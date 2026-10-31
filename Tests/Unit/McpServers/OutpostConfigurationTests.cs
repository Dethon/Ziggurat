using Domain.DTOs.Tunnel;
using Domain.Tools.Files;
using Mcp.Hosting;
using McpServerOutpost.Modules;
using McpServerOutpost.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Shouldly;

namespace Tests.Unit.McpServers;

// The outpost is the one server configured by what somebody types, because installing it is a copy
// and starting it is one line. That puts it at odds with the default configuration order, where the
// command line sits underneath the environment — so the precedence is the thing worth pinning, and
// so is what it makes of the tunnel it opens: the hub's tunnel route, the server name its credential
// was minted for, and the credential that never arrives as a flag.
public class OutpostConfigurationTests
{
    private const string Credential = "minted-for-outpost-laptop";

    private static readonly string[] _minimal =
        ["--name", "laptop", "--dir", "/home/someone/project", "--hub", "https://assistants.example"];

    private static readonly Dictionary<string, string?> _withCredential = new() { ["Credential"] = Credential };

    [Fact]
    public void TheFlags_AreRead()
    {
        var settings = Bind(_minimal);

        settings.Name.ShouldBe("laptop");
        settings.WorkingDirectory.ShouldBe("/home/someone/project");
        settings.Hub.ShouldBe("https://assistants.example");
    }

    // The specific failure this avoids: a `Jailed` environment variable that happens to be set on
    // somebody's machine silently overriding the --jailed they typed. The host builder puts the
    // command line underneath the environment, so this does not come for free.
    //
    // The keys the outpost binds are short and ordinary words, which is what makes the collision
    // realistic rather than theoretical — `NAME` is already set on a WSL shell, and a machine named
    // by the environment instead of by its operator is exactly the confusion this stops.
    [Theory]
    [InlineData("Jailed", "false")]
    [InlineData("Name", "somebody-elses-machine")]
    [InlineData("Hub", "https://somewhere.else")]
    public void AFlagTheOperatorTyped_BeatsAnEnvironmentVariableOfTheSameName(
        string key, string environmentValue)
    {
        var settings = Bind(
            [.. _minimal, "--jailed"],
            new Dictionary<string, string?>(_withCredential) { [key] = environmentValue });

        settings.Jailed.ShouldBeTrue();
        settings.Name.ShouldBe("laptop");
        settings.Hub.ShouldBe("https://assistants.example");
    }

    // An environment variable still wins where no flag was typed, because it is only being asked
    // to fill a gap rather than to overrule somebody.
    [Fact]
    public void AnEnvironmentVariableForAFlagNobodyTyped_IsStillRead()
    {
        Bind(_minimal, new Dictionary<string, string?>(_withCredential) { ["Ext"] = ".zig" })
            .AllowedExtensions().ShouldBe([".zig"]);
    }

    // `--jailed`, not `--jailed true`: that is what a person types, and the command-line
    // configuration provider would otherwise swallow the next flag as the switch's value or drop
    // a trailing one on the floor.
    [Theory]
    [InlineData("--jailed")]
    [InlineData("--jailed=true")]
    public void ABooleanFlagOnItsOwn_ReadsAsTrue(string flag)
    {
        Bind([.. _minimal, flag]).Jailed.ShouldBeTrue();
    }

    [Fact]
    public void WithNoFlagsBeyondTheRequiredOnes_NothingIsJailedOrExecuting()
    {
        var settings = Bind(_minimal);

        settings.Jailed.ShouldBeFalse();
        settings.Exec.ShouldBeFalse();
    }

    [Fact]
    public void WithNoExtensionOverride_TheMachineReadsTheSharedTextList()
    {
        Bind(_minimal).AllowedExtensions().ShouldBe(TextFileExtensions.Default);
    }

    // Wholesale, not additive: a machine full of unusual source files is a different answer to
    // "what is text here", not the usual answer plus a few.
    [Fact]
    public void AnExtensionOverride_ReplacesTheSharedListEntirely()
    {
        Bind([.. _minimal, "--ext", ".zig, .odin"]).AllowedExtensions().ShouldBe([".zig", ".odin"]);
    }

    // The binder's own gate, reached through the outpost's wrapper: a missing flag fails startup
    // naming what is missing rather than serving a machine nobody asked for.
    [Theory]
    [InlineData("WorkingDirectory", new[] { "--name", "laptop", "--hub", "https://assistants.example" })]
    [InlineData("Hub", new[] { "--name", "laptop", "--dir", "/home/someone/project" })]
    public void StartingWithoutARequiredFlag_FailsNamingWhatIsMissing(string missing, string[] args)
    {
        Should.Throw<InvalidOperationException>(() => Bind(args)).Message.ShouldContain(missing);
    }

    // The tunnel is opened on the hub's own route for it, so a person types the deployment's address
    // as they know it; a WebSocket URL is taken as the route itself.
    [Theory]
    [InlineData("https://assistants.example", "wss://assistants.example/tunnel")]
    [InlineData("https://assistants.example/", "wss://assistants.example/tunnel")]
    [InlineData("http://localhost:5080", "ws://localhost:5080/tunnel")]
    [InlineData("wss://assistants.example/tunnel", "wss://assistants.example/tunnel")]
    public void TheTunnel_IsOpenedOnTheHubsTunnelRoute(string hub, string tunnel)
    {
        var settings = Bind(["--name", "laptop", "--dir", "/home/someone/project", "--hub", hub]);

        Tunnel(settings).Hub.ShouldBe(tunnel);
    }

    // What tells the agent this tunnel is a machine rather than one of the household's own servers is
    // the name its credential was minted for, and the tunnel claims exactly that name.
    [Fact]
    public void TheTunnel_ClaimsTheOutpostsServerNameWithTheCredentialFromTheEnvironment()
    {
        var tunnel = Tunnel(Bind(_minimal));

        tunnel.ServerName.ShouldBe("outpost-laptop");
        tunnel.Credential.ShouldBe(Credential);
        tunnel.IsConfigured.ShouldBeTrue();
    }

    // The timings are the shared policy file's, which every tunnelled server and the relay keep.
    [Fact]
    public void TheTunnelsTimings_AreTheSharedPolicyFiles()
    {
        Tunnel(Bind(_minimal)).ReconnectAtMost.ShouldBe(TimeSpan.FromMinutes(1));
    }

    // A name no credential can be minted for fails at startup with a message, rather than as a tunnel
    // the relay refuses forever.
    [Theory]
    [InlineData("LAPTOP")]
    [InlineData("my laptop")]
    [InlineData("laptop-")]
    public void ANameNoCredentialCanBeMintedFor_IsRefusedAtStartup(string name)
    {
        Should.Throw<InvalidOperationException>(() =>
                Bind(["--name", name, "--dir", "/home/someone/project", "--hub", "https://assistants.example"]))
            .Message.ShouldContain("outpost-");
    }

    [Fact]
    public void StartingWithNoCredential_FailsSayingWhereItGoes()
    {
        Should.Throw<InvalidOperationException>(() => Bind(_minimal, []))
            .Message.ShouldContain("CREDENTIAL");
    }

    // The one value that is not a flag, and the reason is the reason it is a secret: a command
    // line is visible to every process on the machine. The flags sit above everything else, so
    // without taking this from underneath them --credential would quietly work. The retired shared
    // secret's spellings are refused the same way: somebody typing one is typing a secret.
    [Theory]
    [InlineData("--credential", "typed-in-the-open")]
    [InlineData("--credential=typed-in-the-open", "--exec")]
    [InlineData("--sharedSecret", "typed-in-the-open")]
    [InlineData("--shared-secret", "typed-in-the-open")]
    public void TheCredential_TypedAsAFlag_IsRefusedRatherThanIgnored(string first, string second)
    {
        Should.Throw<InvalidOperationException>(() => Bind([.. _minimal, first, second]))
            .Message.ShouldContain("CREDENTIAL");
    }

    // An outpost opens its own tunnel and nothing dials it, so the flags that said where to dial it
    // are refused with a message rather than ignored: an operator upgrading must learn they are gone.
    [Theory]
    [InlineData("--advertise", "192.168.1.20")]
    [InlineData("--port", "8099")]
    [InlineData("--port=8099", "--exec")]
    public void ARetiredDialBackFlag_IsRefusedWithAMessage(string first, string second)
    {
        Should.Throw<InvalidOperationException>(() => Bind([.. _minimal, first, second]))
            .Message.ShouldContain("no longer an outpost flag");
    }

    private static TunnelSettings Tunnel(OutpostSettings settings) => ((IMcpHostSettings)settings).Mcp.Tunnel;

    private static OutpostSettings Bind(string[] args, Dictionary<string, string?>? environment = null) =>
        new BuilderWithFakeEnvironment(environment ?? _withCredential).GetOutpostSettings(args);

    // The real process environment must never reach these tests — a CREDENTIAL or NAME exported in
    // the shell running them would shadow the fake — and no test may write to it either, because the
    // suite runs in parallel in one process. So the fake is swapped in for the environment source
    // itself: it lands exactly where AddEnvironmentVariables lands, and precedence is measured
    // against the real stack.
    private sealed class BuilderWithFakeEnvironment(Dictionary<string, string?> environment)
        : IConfigurationBuilder
    {
        private readonly ConfigurationBuilder _inner = new();

        public IDictionary<string, object> Properties => _inner.Properties;
        public IList<IConfigurationSource> Sources => _inner.Sources;

        public IConfigurationBuilder Add(IConfigurationSource source)
        {
            _inner.Add(source is EnvironmentVariablesConfigurationSource
                ? new MemoryConfigurationSource { InitialData = environment }
                : source);
            return this;
        }

        public IConfigurationRoot Build() => _inner.Build();
    }
}