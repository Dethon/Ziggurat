using Mcp.Hosting;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<McpSettings>();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();