using Mcp.Hosting;
using McpServerSugary.Modules;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<SugarySettings>();
builder.Services.ConfigureSugary(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();