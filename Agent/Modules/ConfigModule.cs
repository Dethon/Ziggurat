using System.CommandLine;
using Agent.Settings;
using RetentionSettings = Domain.DTOs.RetentionSettings;

namespace Agent.Modules;

public static class ConfigModule
{
    public static AgentSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddJsonFile(RetentionSettings.FileName, optional: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<AgentSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureAgents(
        this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams, IConfiguration config)
    {
        return services
            .AddAgent(settings)
            .AddSubAgents(settings.SubAgents)
            .AddMemory(config)
            .AddChatMonitoring(settings, cmdParams);
    }

    public static CommandLineParams GetCommandLineParams(string[] args)
    {
        var chatOption = new Option<ChatInterface>("--chat")
        {
            Description = "Chat interface to interact with the agent",
            Required = false,
            DefaultValueFactory = _ => ChatInterface.Web
        };
        var promptOption = new Option<string?>("--prompt", "-p")
        {
            Description = "Run in one-shot mode with the specified prompt",
            Required = false
        };
        var reasoningOption = new Option<bool>("--reasoning", "-r")
        {
            Description = "Show reasoning output from the AI model",
            Required = false,
            DefaultValueFactory = _ => false
        };
        var rootCommand = new RootCommand("Agent Application")
        {
            chatOption,
            promptOption,
            reasoningOption
        };

        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        parseResult.ThrowIfErrors();
        parseResult.ThrowIfSpecialOption();

        var prompt = parseResult.GetValue(promptOption);
        var chatInterface = prompt is not null
            ? ChatInterface.OneShot
            : parseResult.GetValue(chatOption);

        return new CommandLineParams
        {
            ChatInterface = chatInterface,
            Prompt = prompt,
            ShowReasoning = parseResult.GetValue(reasoningOption)
        };
    }

    extension(ParseResult parseResult)
    {
        private void ThrowIfErrors()
        {
            if (parseResult.Errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid command line arguments");
            }
        }

        private void ThrowIfSpecialOption()
        {
            var helpOption = parseResult.GetValue<bool>("--help");
            var versionOption = parseResult.GetValue<bool>("--version");
            if (helpOption || versionOption)
            {
                throw new InvalidOperationException("Invalid command line arguments");
            }
        }
    }
}