using Accounts;
using Accounts.Commands;
using Accounts.Settings;

// A verb's arguments are not configuration: `mint-link --user <id>` would read as switches.
var command = AccountsCommand.IsCommand(args);
var builder = WebApplication.CreateBuilder(command ? [] : args);
var settings = AccountsSettings.Read(builder.Configuration);
if (settings.Missing() is { Count: > 0 } missing)
{
    await Console.Error.WriteLineAsync($"accounts cannot start without: {string.Join(", ", missing)}");
    return 2;
}

var clock = new AccountsClock(TimeProvider.System);
if (command)
{
    return await AccountsCommand.RunAsync(args, settings, clock, Console.Out, CancellationToken.None);
}

builder.Services.AddAccounts(settings, clock);

var app = builder.Build();
app.MapAccounts();

await app.RunAsync();
return 0;