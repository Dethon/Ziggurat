using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Tests.Unit;

// The servers the suite starts in process log to the test process's console. At Information that was
// every SQL statement EF ran: about seventeen thousand lines in one run of the Sugary and accounts tests.
public class TestConsoleLoggingTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, false)]
    [InlineData("McpServerSugary.Glucose.GlucosePoller", LogLevel.Information, false)]
    [InlineData("McpServerSugary.Glucose.GlucosePoller", LogLevel.Warning, true)]
    [InlineData("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", LogLevel.Warning, false)]
    [InlineData("Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware", LogLevel.Warning, false)]
    public void AServerStartedInTheSuite_WritesToTheConsoleOnlyWhatIsWorthReading(
        string category, LogLevel level, bool written)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        using var services = builder.Services.BuildServiceProvider();

        services.GetRequiredService<ILoggerFactory>().CreateLogger(category).IsEnabled(level).ShouldBe(written);
    }
}