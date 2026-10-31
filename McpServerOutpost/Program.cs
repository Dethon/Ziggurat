using System.Net;
using McpServerOutpost.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetOutpostSettings(args);
builder.Services.ConfigureMcp(settings);

// Loopback, on whatever port the system hands out: nothing dials this machine. The outpost opens its
// own tunnel to the hub, and every stream the relay opens inside it becomes a connection to this
// listener from this same process (the hosting library's TunnelClient reads the port once bound). So
// a machine on somebody's network offers its filesystem — and, where they asked for it, a shell — on
// no interface anybody else can reach.
builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));

var app = builder.Build();

// The gates are the hosting library's, as for every tunnelled server: /mcp asks for the tunnel
// credential the relay presents, and every tool call's token is checked against the keys the relay
// hands over the tunnel.
app.MapMcp("/mcp");

await app.RunAsync();