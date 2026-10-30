using HouseholdMigration;
using Microsoft.Extensions.Configuration;

var settings = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build()
    .Get<MigrationSettings>() ?? new MigrationSettings();

return await HouseholdMigrationCommand.RunAsync(args, settings, Console.Out, CancellationToken.None);