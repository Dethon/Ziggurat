using Domain.Tools.Files;
using McpServerOutpost.Modules;
using McpServerOutpost.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpServers;

// The outpost is the one server configured by what somebody types, because installing it is a copy
// and starting it is one line. That puts it at odds with the default configuration order, where the
// command line sits underneath the environment — so the precedence is the thing worth pinning.
public class OutpostConfigurationTests
{
    private static readonly string[] _minimal = ["--name", "laptop", "--dir", "/home/someone/project"];

    [Fact]
    public void TheFlags_AreRead()
    {
        var settings = Bind(["--name", "laptop", "--dir", "/home/someone/project", "--port", "9100"]);

        settings.Name.ShouldBe("laptop");
        settings.WorkingDirectory.ShouldBe("/home/someone/project");
        settings.Port.ShouldBe(9100);
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
    [InlineData("Port", "1234")]
    public void AFlagTheOperatorTyped_BeatsAnEnvironmentVariableOfTheSameName(
        string key, string environmentValue)
    {
        var settings = Bind(
            ["--name", "laptop", "--dir", "/home/someone/project", "--jailed", "--port", "9100"],
            new Dictionary<string, string?> { [key] = environmentValue });

        settings.Jailed.ShouldBeTrue();
        settings.Name.ShouldBe("laptop");
        settings.Port.ShouldBe(9100);
    }

    // An environment variable still wins where no flag was typed, because it is only being asked
    // to fill a gap rather than to overrule somebody.
    [Fact]
    public void AnEnvironmentVariableForAFlagNobodyTyped_IsStillRead()
    {
        Bind(_minimal, new Dictionary<string, string?> { ["Port"] = "9200" }).Port.ShouldBe(9200);
    }

    // `--jailed`, not `--jailed true`: that is what a person types, and the command-line
    // configuration provider throws on a switch with no value.
    [Theory]
    [InlineData("--jailed")]
    [InlineData("--jailed=true")]
    public void ABooleanFlagOnItsOwn_ReadsAsTrue(string flag)
    {
        Bind([.. _minimal, flag]).Jailed.ShouldBeTrue();
    }

    [Fact]
    public void WithNoFlagsBeyondTheRequiredOnes_NothingIsJailedOrExecutingAndThePortIsTheDocumentedDefault()
    {
        var settings = Bind(_minimal);

        settings.Jailed.ShouldBeFalse();
        settings.Exec.ShouldBeFalse();
        settings.Port.ShouldBe(OutpostSettings.DefaultPort);
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
    // naming what is missing rather than serving a machine nobody asked for. Asked of --dir rather
    // than --name because the real process environment is in this stack and a bare `NAME` is set on
    // an ordinary shell, which would fill the gap and hide the gate.
    [Fact]
    public void StartingWithNoWorkingDirectory_FailsNamingWhatIsMissing()
    {
        Should.Throw<InvalidOperationException>(() => Bind(["--name", "laptop"]))
            .Message.ShouldContain("WorkingDirectory");
    }

    // The one value that is not a flag, and the reason is the reason it is a secret: a command
    // line is visible to every process on the machine. The flags sit above everything else, so
    // without taking this from underneath them --sharedSecret would quietly work.
    [Theory]
    [InlineData("--sharedSecret", "typed-in-the-open")]
    [InlineData("--shared-secret", "typed-in-the-open")]
    [InlineData("--sharedSecret=typed-in-the-open", "--exec")]
    public void TheSharedSecret_TypedAsAFlag_IsRefusedRatherThanIgnored(string first, string second)
    {
        Should.Throw<InvalidOperationException>(() => Bind([.. _minimal, first, second]))
            .Message.ShouldContain("SHAREDSECRET");
    }

    [Fact]
    public void TheSharedSecret_ComesFromTheEnvironment()
    {
        Bind(_minimal, new Dictionary<string, string?> { ["SharedSecret"] = "from-the-environment" })
            .SharedSecret.ShouldBe("from-the-environment");
    }

    private static OutpostSettings Bind(string[] args, Dictionary<string, string?>? environment = null) =>
        new ConfigurationBuilder()
            // Stands in for the process environment, which BindSettings reads for real and no test
            // may write to: it is added first, exactly where AddEnvironmentVariables lands.
            .AddInMemoryCollection(environment ?? [])
            .GetOutpostSettings(args);
}