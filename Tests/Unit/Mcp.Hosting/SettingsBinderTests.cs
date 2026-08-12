using Domain.DTOs;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// The one place a server's configuration is read, tested over a configuration builder rather than a
// booted server: precedence, nested binding and validation are all unreachable from a seam that
// starts with an already-bound settings object.
public class SettingsBinderTests : IDisposable
{
    private const string SearchKeyEnvironmentVariable = "Search__ApiKey";
    private const string SolverKeyEnvironmentVariable = "Solver__ApiKey";

    // A secrets id of this test's own, so writing a secret cannot touch the one the test project
    // really uses.
    private readonly string _secretsId = $"ziggurat-bindsettings-{Guid.NewGuid()}";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SearchKeyEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(SolverKeyEnvironmentVariable, null);

        var directory = Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(_secretsId))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ADR-0005, and the assertion most likely to be broken by someone tidying the order toward the
    // framework default. DockerCompose/.env ships every secret as an empty placeholder and compose
    // exports an empty value as an empty string, so a secret that lost to an environment variable
    // would be blanked on every containerised deployment — silently, because several settings read
    // an empty string as "feature not configured".
    [Fact]
    public void AUserSecret_BeatsAnEnvironmentVariableWithTheSameKey()
    {
        Environment.SetEnvironmentVariable(SearchKeyEnvironmentVariable, "from-the-environment");
        WriteUserSecret("""{ "Search:ApiKey": "from-the-secret" }""");

        var settings = new ConfigurationBuilder().BindSettings<ProbeSettings>(_secretsId);

        settings.Search.ApiKey.ShouldBe("from-the-secret");
    }

    // The claim the deleted explicit re-bind in the web-search server was written for. A nested
    // section binds from the environment through the plain call, so there is nothing to re-bind.
    [Fact]
    public void ANestedOptionalSection_BindsFromAnEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(SolverKeyEnvironmentVariable, "solver-key");

        var settings = Bind(("Search:ApiKey", "brave-key"));

        settings.Solver!.ApiKey.ShouldBe("solver-key");
    }

    [Fact]
    public void AnAbsentOptionalSection_StaysNull() =>
        Bind(("Search:ApiKey", "brave-key")).Solver.ShouldBeNull();

    // What replaces the guard that never fired. A genuinely missing section used to come out as a
    // null sub-record and surface later as a NullReferenceException from wherever the value was
    // first read, with nothing in the message naming the missing key.
    [Fact]
    public void AMissingRequiredSection_FailsNamingIt() =>
        Should.Throw<InvalidOperationException>(() => Bind())
            .Message.ShouldContain("Search");

    [Fact]
    public void AMissingRequiredMemberOfASection_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() => Bind(("Search:ApiUrl", "https://example")))
            .Message.ShouldContain("Search.ApiKey");

    // Null only, never empty. McpChannelServiceBus's connection string, Telegram's bot tokens,
    // WebSearch's Brave key, Home Assistant's token, Idealista's key and secret and Library's
    // Jackett/qBittorrent credentials all ship as "" in appsettings.json and are filled from
    // secrets, and an empty CapSolver key is how that feature is switched off. Empty-is-invalid
    // would refuse to start six shipped servers.
    [Fact]
    public void AnEmptyRequiredMember_Binds() =>
        Bind(("Search:ApiKey", "")).Search.ApiKey.ShouldBe("");

    // A required int has no null to reveal an absent key: it binds to 0 and passes a null-only
    // walk. The sandbox server's MaxTimeoutSeconds is the shipped case — a deployment missing it
    // would start cleanly and then every fs_exec would throw from Math.Clamp(…, 1, 0).
    [Fact]
    public void AnAbsentRequiredValueType_FailsNamingTheMember() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder().BindSettings<ProbeLimitsSettings>(_secretsId))
            .Message.ShouldContain("MaxTimeoutSeconds");

    // The counterpart that keeps the check honest: presence in configuration is what is validated,
    // never the bound value, so a deliberate 0 stays legal even though it equals the type default.
    [Fact]
    public void AnExplicitDefaultForARequiredValueType_Binds() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MaxTimeoutSeconds"] = "0" })
            .BindSettings<ProbeLimitsSettings>(_secretsId)
            .MaxTimeoutSeconds.ShouldBe(0);

    // An initializer default is the settings type saying "leave this out and take this value", so a
    // required value type that carries one is not missing when configuration omits it. Voice's six
    // defaulted sub-records are the shipped shape: adding a required int with a default to any of
    // them used to fail startup on every deployment that never wrote the section.
    [Fact]
    public void ADefaultedRequiredValueType_BindsItsDefault() =>
        new ConfigurationBuilder()
            .BindSettings<ProbeDefaultedSectionSettings>(_secretsId)
            .Tts.SpeedPercent.ShouldBe(100);

    // A record struct section is a section like any other, and its required members are the same
    // hole the required-value-type check closed, one level down: the walk used to stop at the struct
    // itself and never look inside it.
    [Fact]
    public void AnAbsentRequiredValueTypeInAStructSection_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Window:Label"] = "x" })
                    .BindSettings<ProbeStructSectionSettings>(_secretsId))
            .Message.ShouldContain("Window.Seconds");

    [Fact]
    public void AFullyConfiguredStructSection_Binds() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Window:Seconds"] = "30" })
            .BindSettings<ProbeStructSectionSettings>(_secretsId)
            .Window.Seconds.ShouldBe(30);

    // A property with no setter is computed, not configuration: nothing binds into it, so validating
    // it says nothing about the deployment. SignalR's WebPush.IsConfigured and Home Assistant's
    // McpSettings.IsConfigured are the shipped shape, and a section-typed one that hands back a
    // fresh instance of its own type walks forever — a StackOverflowException at startup, which no
    // catch block can turn into a message.
    [Fact]
    public void AComputedSectionProperty_IsNotWalked() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ChannelId"] = "probe" })
            .BindSettings<ProbeComputedSettings>(_secretsId)
            .ChannelId.ShouldBe("probe");

    // The presence check must follow the section path, not the display path: a member two levels
    // down lives at "Output:CapBytes", and a walk that asked the root for "Output.CapBytes" would
    // flag every nested value type as absent.
    [Fact]
    public void AnAbsentRequiredValueTypeInASection_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MaxTimeoutSeconds"] = "5",
                        ["Output:Label"] = "x"
                    })
                    .BindSettings<ProbeLimitsSettings>(_secretsId))
            .Message.ShouldContain("Output.CapBytes");

    [Fact]
    public void ANestedRequiredValueTypePresentInConfig_Binds() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MaxTimeoutSeconds"] = "5",
                ["Output:CapBytes"] = "0"
            })
            .BindSettings<ProbeLimitsSettings>(_secretsId)
            .Output!.CapBytes.ShouldBe(0);

    // Telegram constructs BotRegistry(settings.Bots) at registration time, so a bot whose token
    // never bound used to blow up anonymously inside new TelegramBotClient(null). The walk must
    // reach into the element and fail startup by the indexed name.
    [Fact]
    public void AMissingRequiredMemberOfACollectionElement_FailsNamingTheIndexedPath() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Bots:0:AgentId"] = "nabu" })
                    .BindSettings<ProbeFleetSettings>(_secretsId))
            .Message.ShouldContain("Bots[0].BotToken (environment variable Bots__0__BotToken)");

    // The null-only rule holds inside elements too: Telegram's bot tokens ship as "" and are
    // filled from secrets.
    [Fact]
    public void AnEmptyRequiredMemberOfACollectionElement_Binds() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bots:0:AgentId"] = "nabu",
                ["Bots:0:BotToken"] = ""
            })
            .BindSettings<ProbeFleetSettings>(_secretsId)
            .Bots[0].BotToken.ShouldBe("");

    // A nested settings type does not have to live in the same assembly as the settings root — a
    // server can bind a shared Domain record straight into its own settings. IsSection used to
    // require assembly equality with TSettings, so a section like this walked past validation
    // entirely and a missing required member surfaced later as a bare null deep in the server
    // instead of failing startup by name.
    [Fact]
    public void AMissingRequiredMemberOfANestedSectionFromAnotherAssembly_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["SubAgent:Id"] = "researcher",
                        ["SubAgent:Model"] = "anthropic/claude",
                        ["SubAgent:McpServerEndpoints:0"] = "http://localhost/mcp"
                    })
                    .BindSettings<ProbeSubAgentSettings>(_secretsId))
            .Message.ShouldContain("SubAgent.Name");

    // The framework-type exclusion is a namespace check, and a prefix match with no trailing dot
    // reads "SystemX" as the BCL. A settings record whose namespace merely starts with those letters
    // is ordinary application configuration, so skipping it would switch off the startup gate
    // silently — the walk would simply not look inside the section.
    [Fact]
    public void AMissingRequiredMemberOfASectionInASystemNearMissNamespace_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() => BindNearMiss<SystemX.ProbeNearMissSettings>())
            .Message.ShouldContain("Search.ApiKey");

    [Fact]
    public void AMissingRequiredMemberOfASectionInAMicrosoftNearMissNamespace_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() => BindNearMiss<MicrosoftX.ProbeNearMissSettings>())
            .Message.ShouldContain("Search.ApiKey");

    private void BindNearMiss<TSettings>() where TSettings : class =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ApiUrl"] = "https://example" })
            .BindSettings<TSettings>(_secretsId);

    // Voice's satellites bind as a dictionary and are materialised into SatelliteRegistry at
    // registration time, so the walk has to follow dictionary keys the same way it follows indexes.
    [Fact]
    public void AMissingRequiredMemberOfADictionaryElement_FailsNamingTheKeyedPath() =>
        Should.Throw<InvalidOperationException>(() =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Satellites:kitchen:Identity"] = "kitchen-sat"
                    })
                    .BindSettings<ProbeSatelliteSettings>(_secretsId))
            .Message.ShouldContain("Satellites[kitchen].Room");

    // The lower bound of the precedence chain: environment variables are added by BindSettings
    // after the caller's file sources, so an environment variable beats an appsettings value.
    [Fact]
    public void AnEnvironmentVariable_BeatsAnAppSettingsValue()
    {
        Environment.SetEnvironmentVariable(SearchKeyEnvironmentVariable, "from-the-environment");

        Bind(("Search:ApiKey", "from-appsettings")).Search.ApiKey.ShouldBe("from-the-environment");
    }

    // Five servers ask for user secrets from a project with no UserSecretsId. The source is simply
    // absent for them, which is exactly today's behaviour, and they must keep starting.
    [Fact]
    public void NoUserSecretsId_DoesNotThrow() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ApiKey"] = "brave-key" })
            .BindSettings<ProbeSettings>(userSecretsId: null)
            .Search.ApiKey.ShouldBe("brave-key");

    // The shipping call reads the id off the entry assembly, which under the test runner is the
    // test host — an assembly with no UserSecretsId, the same state five servers are in.
    [Fact]
    public void TheEntryAssemblysSecretsId_IsWhatTheShippingCallUses() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ApiKey"] = "brave-key" })
            .BindSettings<ProbeSettings>()
            .Search.ApiKey.ShouldBe("brave-key");

    // Stands in for appsettings.json, which is where the shipped empty placeholders live.
    private ProbeSettings Bind(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .BindSettings<ProbeSettings>(_secretsId);

    private void WriteUserSecret(string json)
    {
        var path = PathHelper.GetSecretsPathFromSecretsId(_secretsId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}

// Shaped like the web-search server: one required section, one optional one that a deployment
// without the feature simply leaves out.
public record ProbeSettings
{
    public required ProbeSearchConfig Search { get; init; }

    public ProbeSolverConfig? Solver { get; init; }
}

public record ProbeSearchConfig
{
    public required string ApiKey { get; init; }

    public string ApiUrl { get; init; } = "https://example";
}

public record ProbeSolverConfig
{
    public required string ApiKey { get; init; }
}

// Shaped like the Telegram server: a required array whose elements carry required members, read at
// registration time by BotRegistry.
public record ProbeFleetSettings
{
    public required ProbeBotConfig[] Bots { get; init; }
}

public record ProbeBotConfig
{
    public required string AgentId { get; init; }

    public required string BotToken { get; init; }
}

// Shaped like the voice server: satellites keyed by name, read at registration time by
// SatelliteRegistry.
public record ProbeSatelliteSettings
{
    public Dictionary<string, ProbeSatelliteConfig> Satellites { get; init; } = new();
}

public record ProbeSatelliteConfig
{
    public required string Identity { get; init; }

    public required string Room { get; init; }
}

// Shaped like a server that binds a shared Domain record straight into its own settings, rather
// than a record declared alongside the settings root.
public record ProbeSubAgentSettings
{
    public required SubAgentDefinition SubAgent { get; init; }
}

// Shaped like the sandbox server: required value types that a null-only walk cannot see missing,
// one of them nested so the presence check has to navigate configuration sections.
public record ProbeLimitsSettings
{
    public required int MaxTimeoutSeconds { get; init; }

    public ProbeOutputConfig? Output { get; init; }
}

public record ProbeOutputConfig
{
    public required int CapBytes { get; init; }

    public string? Label { get; init; }
}

// Shaped like voice's six defaulted sub-records: a section whose initializer supplies every member,
// so a deployment that never writes the section is complete rather than misconfigured.
public record ProbeDefaultedSectionSettings
{
    public ProbeTtsConfig Tts { get; init; } = new() { Voice = "kokoro", SpeedPercent = 100 };
}

public record ProbeTtsConfig
{
    public required string Voice { get; init; }

    public required int SpeedPercent { get; init; }
}

// A settings section that happens to be a struct. Nothing about being a value type makes its
// required members any less required.
public record ProbeStructSectionSettings
{
    public ProbeWindowConfig Window { get; init; }
}

// A settings type with a computed section-typed property, the shape that recurses forever: every
// read of Reloaded is a new instance, so a walk that follows it never runs out of instances.
public record ProbeComputedSettings
{
    public required string ChannelId { get; init; }

    public ProbeComputedSettings Reloaded => this with { ChannelId = ChannelId.Trim() };
}

public readonly record struct ProbeWindowConfig
{
    public required int Seconds { get; init; }

    public string? Label { get; init; }
}