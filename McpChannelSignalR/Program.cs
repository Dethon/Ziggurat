using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Settings;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<ChannelSettings>();
builder.Services.ConfigureChannel(settings);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors();
app.MapHub<ChatHub>("/hubs/chat");
AttachmentEndpoints.Map(app);
app.MapMcp("/mcp");

await app.RunAsync();