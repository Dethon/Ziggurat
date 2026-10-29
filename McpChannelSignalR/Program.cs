using Infrastructure.Extensions;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Dictation;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Settings;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<ChannelSettings>();
builder.Services.ConfigureChannel(settings);
builder.Services.AddDeploymentCors(settings.Cors);

var app = builder.Build();
app.UseCors();
app.MapHub<ChatHub>("/hubs/chat");
AttachmentEndpoints.Map(app);
DictationEndpoints.Map(app);
app.MapMcp("/mcp");

await app.RunAsync();