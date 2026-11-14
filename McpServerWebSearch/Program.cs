using Mcp.Hosting;
using McpServerWebSearch.Handoff;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<McpSettings>();
// Two listeners: /mcp for the deployment's network, the /browse pages for the proxy
// (PagesListener keeps each to its own).
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(settings.Listeners.Mcp);
    options.ListenAnyIP(settings.Listeners.Pages);
});
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.UseWebSockets();
app.MapMcp("/mcp");
app.MapHandoff();

await app.RunAsync();