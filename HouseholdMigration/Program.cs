using HouseholdMigration;
using Microsoft.Extensions.Configuration;

// Environment variables over the settings' defaults, and no appsettings.json: the Sugary server's,
// which comes with its project, sits in this output too and configures that server, not this run.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

// The home's move into the tenant kit (its `state-move` service) is a verb of its own; anything else
// is the household migration.
if (args is [HomeStateMove.Verb, .. var move])
{
    return await HomeStateMove.RunAsync(
        move, configuration.GetSection("HomeState").Get<HomeStateSettings>() ?? new HomeStateSettings(),
        Console.Out, CancellationToken.None);
}

var settings = configuration.Get<MigrationSettings>() ?? new MigrationSettings();

return await HouseholdMigrationCommand.RunAsync(args, settings, Console.Out, CancellationToken.None);