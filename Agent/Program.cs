using Agent.App;
using Agent.Modules;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;

var builder = WebApplication.CreateBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();

builder.Services.ConfigureAgents(settings, cmdParams, builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors();

app.MapGet("/api/agents", (IAgentDefinitionProvider provider, string? userId) =>
    provider.GetAll(userId).Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)));

app.MapPost("/api/agents", (IAgentDefinitionProvider provider, string userId, CustomAgentRegistration registration) =>
{
    var definition = provider.RegisterCustomAgent(userId, registration);
    return new AgentCatalogEntry(definition.Id, definition.Name, definition.Description);
});

app.MapDelete("/api/agents/{agentId}", (IAgentDefinitionProvider provider, string userId, string agentId) =>
    provider.UnregisterCustomAgent(userId, agentId));

app.MapOutposts(settings.Outposts.SharedSecret);

await app.RunAsync();