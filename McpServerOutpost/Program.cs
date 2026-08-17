using System.Net;
using Domain.Outposts;
using McpServerOutpost.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

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

// The other half of the shared secret. This port is on somebody's own computer, listening on every
// interface, offering their whole filesystem and — where they asked for it — a shell; without this
// anyone who could reach it would have all of that for the price of knowing the URL. The same
// secret the machine presents when it registers, compared by the same rule the hub compares it by.
app.Use(async (context, next) =>
{
    if (!OutpostSecret.Matches(context.Request.Headers.Authorization.ToString(), settings.SharedSecret))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapMcp("/mcp");

await app.RunAsync();