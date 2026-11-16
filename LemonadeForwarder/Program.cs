using LemonadeForwarder;
using Mcp.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<ForwarderSettings>();
await ForwarderApp.Build(settings, builder).RunAsync();