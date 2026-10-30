using Agent.App;
using Agent.Modules;
using Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();

builder.Services.ConfigureAgents(settings, cmdParams, builder.Configuration);
builder.Services.AddDeploymentCors(settings.Cors);

var app = builder.Build();
app.UseCors();

app.MapAgents(settings.AgentApi.SharedSecret);
app.MapOutposts(settings.Outposts.SharedSecret);
app.MapVfsBridge();
app.MapAgentKeys();

await app.RunAsync();