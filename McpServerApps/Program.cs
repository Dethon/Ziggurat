using Mcp.Hosting;
using McpServerApps.Modules;
using McpServerApps.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<AppsSettings>();
// Two listeners: /mcp for the deployment's network, app origins for the proxy (AppPagesListener keeps
// each to its own).
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(settings.Listeners.Mcp);
    options.ListenAnyIP(settings.Listeners.Pages);
});
builder.Services.ConfigureApps(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();