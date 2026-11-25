using Mcp.Hosting;
using McpServerApps.Modules;
using McpServerApps.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<AppsSettings>();
builder.Services.ConfigureApps(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();