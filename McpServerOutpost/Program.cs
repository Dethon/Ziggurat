using System.Net;
using McpServerOutpost.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetOutpostSettings(args);
builder.Services.ConfigureMcp(settings);

// Every interface, because the outpost cannot know which one the hub will reach it on — that is
// exactly what --advertise exists to settle, and it settles the address the hub is told, not the
// socket this listens on.
builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, settings.Port));

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();