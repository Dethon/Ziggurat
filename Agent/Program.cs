using Agent.App;
using Agent.Modules;

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

app.MapAgents(settings.AgentApi.SharedSecret);
app.MapOutposts(settings.Outposts.SharedSecret);
app.MapVfsBridge();

await app.RunAsync();