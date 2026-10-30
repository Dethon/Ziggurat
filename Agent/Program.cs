using Agent.App;
using Agent.Commands;
using Agent.Modules;
using Infrastructure.Extensions;

// A verb's arguments are not configuration: `redelegate --yes` would read as a switch. The verb runs
// instead of the agent, over the same settings and the same key volume.
var command = AgentCommand.IsCommand(args);
var builder = WebApplication.CreateBuilder(command ? [] : args);
var settings = builder.Configuration.GetSettings();
if (command)
{
    return await AgentCommand.RunAsync(args, settings, Console.In, Console.Out, CancellationToken.None);
}

var cmdParams = ConfigModule.GetCommandLineParams(args);

builder.Services.ConfigureAgents(settings, cmdParams, builder.Configuration);
builder.Services.AddDeploymentCors(settings.Cors);

var app = builder.Build();
app.UseCors();

app.MapAgents(settings.AgentApi.SharedSecret);
app.MapOutposts(settings.Outposts.SharedSecret);
app.MapVfsBridge();
app.MapAgentKeys();

await app.RunAsync();
return 0;