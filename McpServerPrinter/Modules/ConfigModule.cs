using Domain.Contracts;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Clients.Printer;
using Infrastructure.Printing;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerPrinter.McpPrompts;
using McpServerPrinter.Services;
using McpServerPrinter.Settings;
using Microsoft.Extensions.DependencyInjection;
using SharpIpp;

namespace McpServerPrinter.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigurePrinter(this IServiceCollection services, PrinterSettings settings)
    {
        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISharpIppClient>(_ => new SharpIppClient())
            .AddSingleton<IPrinterClient>(sp => new IppPrinterClient(
                sp.GetRequiredService<ISharpIppClient>(), new Uri(settings.PrinterUri), settings.DocumentFormat, settings.PrintScaling))
            .AddSingleton<IPrintSpool>(sp => new PrintSpool(settings.SpoolPath, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<PrintQueueGate>()
            .AddSingleton(sp => new PrintQueueCoordinator(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<PrintQueueGate>(),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMilliseconds(settings.SubmitDebounceMilliseconds),
                TimeSpan.FromMilliseconds(settings.ReconcileGraceMilliseconds)))
            .AddSingleton(sp => new PrinterQueueFileSystem(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<PrintQueueGate>(),
                settings.SupportedFormats,
                timeProvider: sp.GetRequiredService<TimeProvider>()))
            .AddHostedService<PrintSubmissionWorker>();

        services
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<PrinterQueueFileSystem>()
            .AddFileSystemResource<PrinterQueueFileSystem>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}