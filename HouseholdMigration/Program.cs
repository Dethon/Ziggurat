using HouseholdMigration;
using Microsoft.Extensions.Configuration;

// Environment variables over MigrationSettings' defaults, and no appsettings.json: the Sugary server's,
// which comes with its project, sits in this output too and configures that server, not this run.
var settings = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build()
    .Get<MigrationSettings>() ?? new MigrationSettings();

return await HouseholdMigrationCommand.RunAsync(args, settings, Console.Out, CancellationToken.None);