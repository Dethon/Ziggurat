using Mcp.Hosting;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<McpSettings>();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp("/mcp");
WatchFiredEndpoint.Map(app);

await app.RunAsync();