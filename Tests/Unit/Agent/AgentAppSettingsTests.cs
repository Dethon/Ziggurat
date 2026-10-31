using System.Reflection;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Domain.Skills;
using Domain.Tools.FileSystem;
using global::Agent.Modules;
using global::Agent.Settings;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Deployment;

namespace Tests.Unit.Agent;

// Guards the appsettings -> definition -> prompt chain, and the cross-key rules that break a
// turn outright. The agents are the home household's own now, seeded by the household migration
// (HouseholdMigration/Home/agents.json, read here as the accounts service reads an agents section);
// the settings file keeps the worker profiles, the channel defaults and the shared servers. Deliberately not the values the file happens to carry: reading a value back
// off the same file only fails when someone changes it on purpose, and then the test is edited
// to match -- friction, never a defect caught. What is left is the wiring that fails silently
// and the couplings no single reader would spot.
public class AgentAppSettingsTests
{
    // Two contracts for the same thing is how the drift got in: a relative rule sitting beside
    // the absolute one lets a short transcript surrounded by English resolve to English. The rule
    // it used to sit in is now a typed section, and this is the chain that puts it in front of the
    // model: the agent names the section, and the section carries no competing rule.
    [Fact]
    public void VoiceRules_Nabu_SelectsThemAndTheyCarryNoCompetingRelativeLanguageRule()
    {
        HomeAgents.Definition("nabu").PromptSections.ShouldContain(VoicePrompt.Name);

        VoicePrompt.Instructions.ShouldNotContain("the language the user spoke");
    }

    // One of the few values worth reading back off the file, because it is a rule rather than a
    // setting: an agent that exists to search for downloads has no business reaching a person's
    // laptop, and the opt-in is what keeps a machine appearing on the network from widening what
    // any agent can touch. Turning it on for the download assistant would be a decision, and a
    // decision is what this is here to make somebody state.
    [Fact]
    public void Outposts_TheDownloadAssistant_IsNotOptedIn()
    {
        HomeAgents.Definition("jack").UsesOutposts.ShouldBeFalse();
    }

    // The other half of the same rule, and the delegation path the deployment actually uses: both
    // agents that enable subagents are opted in, so the worker saying yes too is what makes a
    // delegated task reach the machines its parent can already reach.
    [Fact]
    public void Outposts_TheWorkerSubAgent_IsOptedIn()
    {
        BoundSubAgents().Single(a => a.Id == "jonas-worker").UsesOutposts.ShouldBeTrue();
    }

    // An omitted flag means no at either level, which is the whole point of both defaulting to
    // false — a profile added without thinking about machines reaches none of them.
    [Fact]
    public void Bind_ASubAgentDeclaringNoOptIn_ReachesNoOutposts()
    {
        var settings = BindSettings(
            ("subAgents:0:id", "worker"),
            ("subAgents:0:name", "Worker"),
            ("subAgents:0:mcpServerEndpoints:0", "http://localhost"));

        settings.SubAgents.Single().UsesOutposts.ShouldBeFalse();
    }

    // The rest of the chain: appsettings -> AgentDefinition -> system prompt. Every hop binds by
    // convention, so a renamed or dropped key fails nothing at build time -- the agent just
    // quietly goes back to inferring its language from an all-English request.
    [Fact]
    public void Language_Nabu_ReachesTheSystemPromptAsItsLastSection()
    {
        var nabu = HomeAgents.Definition("nabu");
        nabu.Language.ShouldBe("es");

        PromptComposer.Compose(new PromptContext
        {
            AgentId = nabu.Id,
            Name = nabu.Name,
            Description = nabu.Description,
            CustomInstructions = nabu.CustomInstructions,
            Language = nabu.Language,
            Now = DateTimeOffset.UnixEpoch
        })
            .Text
            .ShouldEndWith(LanguagePrompt.Build("es")!);
    }

    // The failure in this file's territory that nobody would notice. Configuration binds by naming
    // convention and nothing sets ErrorOnUnknownConfiguration, so renaming a property on
    // AgentDefinition leaves its JSON key silently ignored -- no error, no warning, the agent just
    // quietly runs on the default. Walking every declared key covers keys added later too, and
    // says nothing about which values they hold.
    [Fact]
    public void Binding_EveryKeyDeclaredInAppSettings_ReachesItsDefinition()
    {
        var subAgents = BoundSubAgents();

        var severed = Declared("subAgents", id => subAgents.Single(a => a.Id == id))
            .Where(binding => binding.Bound is null)
            .Select(binding => binding.Path)
            .ToList();

        severed.ShouldBeEmpty();
    }

    // A household's agents are its configuration's: the file that used to carry them carries none,
    // so nothing in it can disagree with what a household runs.
    [Fact]
    public void TheSettingsFile_CarriesNoAgentDefinitions()
    {
        Root()["agents"].ShouldBeNull();
        Root()["agentDefaults"]!["fallback"].ShouldBeNull();
    }

    // The migration exists to remove the dual-idiom problem; a pasted suffix would bring it back.
    [Fact]
    public void Model_NoAgent_CarriesARoutingSuffix()
    {
        var models = HomeAgents.Definitions().Concat(AgentTemplates.All).Select(a => a.Model);

        models.ShouldAllBe(m => !m.Contains(":nitro") && !m.Contains(":floor"));
    }

    // Relace serves jonas's model and garbles a photograph on the way in: pinned to it, a plate of
    // courgette and pizza came back as a blank page watermarked "diet", "diciembre", a tortilla —
    // seven of eight, where every other provider read it (2026-10-18). Balanced routing reached it
    // often enough that jonas file_read and cropped photos it had been sent, looking for a picture
    // it never got. Ignored rather than routed around with `sort`/`order`, so routing stays balanced.
    // Morph serves both agents' models and loses a tool argument mid-flow: after the first
    // web_action of a form it answers the next ones with the element's ref moved to endRef or left
    // out (pinned to it, 3 of 12 second calls kept it; DeepInfra 12 of 12), so the browser refuses
    // and the model retries to its ceiling. Four web scenarios went 0/4 on it (2026-10-19). Ignored
    // the same way, by both agents: nabu's throughput sort would otherwise prefer it.
    [Fact]
    public void Jonas_IgnoresTheProvidersThatGarblePhotographsAndDropToolArguments()
    {
        Ignored("jonas").ShouldBe(["relace", "morph"]);
    }

    [Fact]
    public void Nabu_IgnoresTheProviderThatDropsToolArguments()
    {
        Ignored("nabu").ShouldBe(["morph"]);
    }

    private static List<string>? Ignored(string agentId) =>
        HomeAgents.Definition(agentId).ProviderRouting?.Ignore?.ToList();

    // A worker runs its parent's model, served by whoever serves its parent. A key naming either
    // here would bind to nothing and read as a setting that works.
    [Fact]
    public void SubAgents_NameNoModelAndNoRoutingOfTheirOwn()
    {
        var declared = Root()["subAgents"]!.AsArray()
            .SelectMany(a => a!.AsObject().Select(key => key.Key));

        declared.ShouldNotContain("model");
        declared.ShouldNotContain("providerRouting");
    }

    // Neighboring binding style (see ProviderRoutingBindingTests): an in-memory IConfiguration
    // isolates the keys under test from the real appsettings.json, so a rename here fails loudly
    // instead of silently reading back whatever the file happens to carry.
    [Fact]
    public void Bind_PatchableModels_BindsIdAndName()
    {
        var settings = BindSettings(
            ("patchableModels:0:id", "openai/gpt-5.6-luna"),
            ("patchableModels:0:name", "GPT Luna"),
            ("patchableModels:1:id", "z-ai/glm-5.2"),
            ("patchableModels:1:name", "GLM 5.2"));

        settings.PatchableModels.ShouldBe([
            new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"),
            new PatchableModel("z-ai/glm-5.2", "GLM 5.2")
        ]);
    }

    // The feature ships off. An address is the one thing that turns it on, and a deployment
    // without a local box must see no lemon entries, no probes and no warnings.
    [Fact]
    public void LemonadeChat_ShipsWithAnEmptyAddress()
    {
        BoundConfig().GetSection("lemonadeChat").Get<LemonadeChatConfiguration>()!.ApiUrl.ShouldBe("");
    }

    [Fact]
    public void Bind_LemonadeChat_BindsTheAddressAndTheOptionalKey()
    {
        var settings = BindSettings(
            ("lemonadeChat:apiUrl", "http://192.168.5.40:13305/api/v1"),
            ("lemonadeChat:apiKey", "secret"));

        settings.LemonadeChat.ApiUrl.ShouldBe("http://192.168.5.40:13305/api/v1");
        settings.LemonadeChat.ApiKey.ShouldBe("secret");
    }

    [Fact]
    public void Bind_LemonadeChatOmitted_MeansTheFeatureIsOff()
    {
        BindSettings().LemonadeChat.ApiUrl.ShouldBe("");
    }

    // Who answers a message whose channel named no agent is configuration, in two halves: the
    // deployment's per channel here, the household's own default in its agents section. A channel
    // default naming an agent the home does not have routes the home's messages nowhere, and the
    // Sugary app's must name an agent every new household has as well, the Sugary template's.
    [Fact]
    public void AgentDefaults_EveryChannelDefault_NamesOneOfTheHomesAgents()
    {
        var defaults = BoundConfig().GetSection("agentDefaults").Get<AgentDefaults>()!;
        var home = HomeAgents.Section();

        defaults.ByChannel.Values.ShouldAllBe(id => home.Agents.Any(a => a.Id == id));
        home.Agents.ShouldContain(a => a.Id == home.DefaultAgent);
        HouseholdAgents.Starting.Select(a => a.Id).ShouldContain(defaults.For("sugary")!);
    }

    // The nested map is the part that fails silently: a renamed key binds to nothing, the
    // household's default answers for every channel, and voice starts speaking with the general
    // assistant.
    [Fact]
    public void Bind_AgentDefaults_BindsThePerChannelMap()
    {
        var settings = BindSettings(("agentDefaults:byChannel:voice", "nabu"));

        settings.AgentDefaults.For("voice").ShouldBe("nabu");
        settings.AgentDefaults.For("telegram").ShouldBeNull();
    }

    // A feature turned off withholds its shared server by the address the agents dial it at, so each
    // is the address the home's agents and the templates name it by.
    [Fact]
    public void SharedServers_AreTheAddressesTheAgentsDialThemAt()
    {
        var shared = BoundConfig().GetSection("sharedServers").Get<SharedServers>()!;
        var dialled = HomeAgents.Definitions().Concat(AgentTemplates.All)
            .SelectMany(a => a.McpServerEndpoints)
            .Where(e => !global::Domain.DTOs.Tunnel.TunnelCredentialProtocol.IsServerName(e))
            .Distinct()
            .ToList();

        shared.All.ShouldBe(dialled, ignoreOrder: true);
    }

    // Her estimates run on an agent of their own that only estimates: the Sugary server is dialled
    // for the estimating words it serves, and nothing reaches the model to call — not her log, not a
    // skill, not memory to recall an old meal into a new estimate, not a worker. A turn that read
    // her log first cost three times as much and sometimes answered nothing (the first paid pass,
    // 2026-10-15). That is a rule about her health data and her bill, not a setting.
    [Fact]
    public void Sugary_DialsTheSugaryServerAloneForItsPrompt_AndCallsNothing()
    {
        foreach (var sugary in (AgentDefinition[])[HomeAgents.Definition("sugary"), AgentTemplates.Named("sugary")!])
        {
            sugary.McpServerEndpoints.ShouldBe(["http://mcp-sugary:8080/mcp"]);
            sugary.NoTools.ShouldBeTrue();
            sugary.EnabledFeatures.ShouldBeEmpty();
            sugary.WhitelistPatterns.ShouldBeEmpty();
            sugary.UsesOutposts.ShouldBeFalse();
        }
    }

    // Her log is Jonas's to read, beside the glucose history /ha serves, so a meal can be lined up
    // with what her glucose did after it. Dialling the server is not consuming its channel: the
    // estimates it sends stay routed to sugary, whoever else reads the mount.
    [Fact]
    public void Jonas_DialsTheSugaryServerForHerLog_AndHerEstimatesStaySugarys()
    {
        var settings = BoundConfig().Get<AgentSettings>()!;
        var jonas = HomeAgents.Definition("jonas");

        jonas.McpServerEndpoints.ShouldContain("http://mcp-sugary:8080/mcp");
        jonas.McpServerEndpoints.ShouldContain("homeassistant");
        settings.AgentDefaults.For("sugary").ShouldBe("sugary");
    }

    // A household's server is named rather than addressed, and one no stack runs under that name is
    // never an error: it is offline, left out of the turn and named in its context. So a misspelt
    // name would only ever show as a context line. Every name the definitions and the channels give
    // is a server the repo has — the one the integration suites and the eval host in its place, by the
    // same name — and a server of the kind it is named as: tools for an agent, a channel for a channel.
    [Fact]
    public void EveryHouseholdServerTheDefinitionsName_IsOneTheTestStacksHostInItsPlace()
    {
        var hosted = McpServerRegistrations.All.ToDictionary(row => row.Id, row => row.Role);
        McpServerRole[] tools = [McpServerRole.Tool, McpServerRole.DualRole];
        McpServerRole[] channels = [McpServerRole.Channel, McpServerRole.DualRole];

        var toolServers = ShippedAgentSettings.ToolServerNames();
        var channelServers = ShippedAgentSettings.ChannelServerNames();

        toolServers.ShouldNotBeEmpty();
        channelServers.ShouldNotBeEmpty();
        toolServers.Where(name => !hosted.TryGetValue(name, out var role) || !tools.Contains(role))
            .ShouldBeEmpty("an agent names a household server no stack hosts as a tool server");
        channelServers.Where(name => !hosted.TryGetValue(name, out var role) || !channels.Contains(role))
            .ShouldBeEmpty("a channel names a household server no stack hosts as a channel");
    }

    // The app's channel is connected, and a meal sent on it is answered by the agent that estimates:
    // left to the fallback it would be answered by the general assistant, in prose no schema reads.
    [Fact]
    public void TheSugaryChannel_IsConnectedAndAnsweredBySugary()
    {
        var settings = BoundConfig().Get<AgentSettings>()!;

        settings.AgentDefaults.For("sugary").ShouldBe("sugary");
        Root()["channelEndpoints"]!.AsArray()
            .Single(e => e!["channelId"]!.GetValue<string>() == "sugary")!["endpoint"]!.GetValue<string>()
            .ShouldBe("http://mcp-sugary:8080/mcp");
    }

    // The Sugary server refuses a photo for a model that cannot see one, and asks the catalogue about
    // the agent its turns are answered by. The message names no agent — routing is agentDefaults' —
    // so the server's setting is a second spelling of that route, and the two must not drift: asked
    // about any other agent, the refusal would judge the wrong model.
    [Fact]
    public void TheSugaryServer_AsksAboutTheAgentItsChannelIsRoutedTo()
    {
        var settings = BoundConfig().Get<AgentSettings>()!;
        var server = JsonNode.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "McpServerSugary", "appsettings.json")))!;

        server["Agent"]!.GetValue<string>().ShouldBe(settings.AgentDefaults.For("sugary"));
    }

    // A label turn's patch is honoured only for a model on the patchable list; off it, the agent
    // would read the label on its own cheaper model and the server would refuse every label.
    [Fact]
    public void TheSugaryServersLabelModel_IsOneTheAgentMaySwitchTo()
    {
        var settings = BoundConfig().Get<AgentSettings>()!;
        var server = JsonNode.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "McpServerSugary", "appsettings.json")))!;

        var labelModel = server["LabelModel"]!.GetValue<string>();
        labelModel.ShouldBe("anthropic/claude-opus-5.5");
        settings.PatchableModels.Select(m => m.Id).ShouldContain(labelModel);
        new global::McpServerSugary.Settings.SugarySettings().LabelModel.ShouldBe(labelModel);
    }

    // Her estimates are read back in WebChat. A review copy names no channel, so the deployment
    // declares the one that shows them: one, so a turn cannot be shown twice, and one that owns its
    // topics, since an attach-only channel has nothing to show a conversation in.
    [Fact]
    public void ReviewCopies_AreHostedByWebChatAlone()
    {
        var hosts = BoundConfig().Get<AgentSettings>()!.ChannelEndpoints.Where(e => e.HostsReviewCopies).ToList();

        var host = hosts.ShouldHaveSingleItem();
        host.ChannelId.ShouldBe("signalr");
        host.AttachOnly.ShouldBeFalse();
    }

    // The binder leaves a required member it found nothing for null, whatever the type says, and the
    // agent used to start anyway and fail on that member's first use, far from its name.
    [Fact]
    public void Bind_ARequiredSettingMissing_FailsAtStartupNamingIt()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["openRouter:apiKey"] = "key",
            ["backCalls:lemonadeUrl"] = "http://lemonade:13305"
        }).Build();

        var settings = config.Get<AgentSettings>()!;

        var refused = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddLogging().AddAgent(settings));

        refused.Message.ShouldContain("openRouter:apiUrl");
        refused.Message.ShouldContain("redis");
        refused.Message.ShouldNotContain("backCalls");
    }

    private static AgentSettings BindSettings(params (string Key, string? Value)[] entries)
    {
        var config = new Dictionary<string, string?>
        {
            ["openRouter:apiUrl"] = "https://openrouter.ai/api/v1/",
            ["openRouter:apiKey"] = "key",
            ["redis:connectionString"] = "redis:6379"
        };
        foreach (var (key, value) in entries)
        {
            config[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build()
            .Get<AgentSettings>()!;
    }

    private static SubAgentDefinition[] BoundSubAgents() =>
        BoundConfig().GetSection("subAgents").Get<SubAgentDefinition[]>()!;

    private static IConfigurationRoot BoundConfig() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "Agent", "appsettings.json"))
            .Build();

    // Every key each entry declares, paired with what the binder put on the matching definition.
    private static IEnumerable<(string Path, object? Bound)> Declared(
        string section, Func<string, object> definitionFor)
    {
        return Root()[section]!.AsArray()
            .Select(entry => entry!.AsObject())
            .SelectMany(entry =>
            {
                var definition = definitionFor(entry["id"]!.GetValue<string>());
                return entry.Select(key => ($"{entry["id"]}.{key.Key}", Bound(definition, key.Key)));
            });
    }

    private static object? Bound(object definition, string key) =>
        definition.GetType()
            .GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(definition);

    // The deadline is spent from the moment Jev is asked. A warm judgment from prod has measured
    // 270–590 ms, so the shipped budget sits above that tail; a cold handshake alone is ~660 ms
    // there, which is what the connection keep-alive exists to spare the turn, not the deadline.
    [Fact]
    public void SkillPreloadDeadline_CoversAWarmJudgmentFromProd()
    {
        var shipped = BoundConfig().GetSection("skillPreload").Get<SkillPreloadSettings>()!;

        shipped.DeadlineMs.ShouldBe(700);
        shipped.DeadlineMs.ShouldBe(new SkillPreloadSettings().DeadlineMs);
    }

    // Every bar of the exec screen is in the shipped file, at the value measured with the shipped
    // wording (ExecScreenJevTests' cases), so production can be re-read against
    // the numbers that chose them rather than against a default nobody wrote down.
    [Fact]
    public void ExecScreen_ShipsEveryBarAtTheProbesValues()
    {
        var section = BoundConfig().GetSection("execScreen");
        var shipped = section.Get<ExecScreenSettings>()!;

        section.GetChildren().Select(c => c.Key).ShouldBe(
            ["enabled", "deadlineMs", "recentRequests", "requestChars", "servesBar", "destroysBar", "sendsOutBar", "runsDownloadedBar"],
            ignoreOrder: true);
        shipped.Enabled.ShouldBeTrue();
        shipped.DeadlineMs.ShouldBe(1500);
        shipped.RecentRequests.ShouldBe(3);
        shipped.RequestChars.ShouldBe(1000);
        shipped.ServesBar.ShouldBe(0.6);
        shipped.DestroysBar.ShouldBe(0.4);
        shipped.SendsOutBar.ShouldBe(0.5);
        shipped.RunsDownloadedBar.ShouldBe(0.5);
        shipped.ShouldBe(new ExecScreenSettings());
    }

    // The local override is where a developer runs the agent against the compose stack, and a
    // channel server it does not list is one whose fires answer 503 there: every channel the
    // shipped file connects, the local one connects too, by the same id.
    [Fact]
    public void LocalOverride_ConnectsEveryChannelTheShippedFileDoes()
    {
        static IEnumerable<string> channelIds(JsonNode root) =>
            root["channelEndpoints"]!.AsArray().Select(e => e!["channelId"]!.GetValue<string>());

        var local = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.Local.json")))!;

        channelIds(local).ShouldBe(channelIds(Root()), ignoreOrder: true);
    }

    private static JsonNode Root()
    {
        // Read the working tree, never AppContext.BaseDirectory: many referenced projects copy
        // their own appsettings.json to the test output, so Tests/bin/.../appsettings.json is
        // whichever one won the copy race -- not the Agent's. File.ReadAllText also strips the
        // UTF-8 BOM this file carries, which would otherwise fail JSON parsing.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.json"));
        return JsonNode.Parse(json)!;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ziggurat.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Ziggurat.sln not found above test directory");
    }
}