using System.Net;
using McpServerOutpost.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetOutpostSettings(args);
builder.Services.ConfigureMcp(settings);

// Every interface, because the outpost cannot know which one the hub will reach it on — that is
// exactly what --advertise exists to settle, and it settles the address the hub is told, not the
// socket this listens on. IPv6Any rather than Any: the dual-mode socket takes IPv4 as well, and
// Any alone would let --advertise name an IPv6 address nothing here listens on — a registration
// that looks exactly like a machine that is asleep, forever.
builder.WebHost.UseKestrel(options => options.Listen(IPAddress.IPv6Any, settings.Port));

var app = builder.Build();

// The other half of the shared secret is the host's gate on /mcp, installed by the hosting library
// with the secret OutpostSettings answers for: this machine's own, the one it registers with. This
// port is on somebody's own computer, listening on every interface, offering their whole filesystem
// and — where they asked for it — a shell, so the gate is the same one every deployment server has
// rather than one written here to drift from it.
app.MapMcp("/mcp");

await app.RunAsync();