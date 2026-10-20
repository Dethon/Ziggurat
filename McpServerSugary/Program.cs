using Mcp.Hosting;
using McpServerSugary.App;
using McpServerSugary.Import;
using McpServerSugary.Modules;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;

// A verb's arguments are not configuration: `import /data/import` would read as a switch.
var command = SugaryCommand.IsCommand(args);
var builder = WebApplication.CreateBuilder(command ? [] : args);
var settings = builder.Configuration.BindSettings<SugarySettings>();
if (command)
{
    return await SugaryCommand.RunAsync(args, settings, Console.Out, CancellationToken.None);
}

builder.Services.ConfigureSugary(settings);

var app = builder.Build();
app.MapMcp("/mcp");
app.MapSugaryApp();

await app.RunAsync();
return 0;